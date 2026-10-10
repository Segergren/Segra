using Serilog;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Shared;

namespace Segra.Backend.Media
{
    /// <summary>
    /// Stores a video's Segra metadata (game, title, bookmarks...) inside the MP4 so import and recovery
    /// can restore it, plus bookmarks as chapters for editors and players. Only the index (moov) is
    /// rewritten, media data never moves.
    /// </summary>
    internal static class EmbeddedMetadataService
    {
        private static readonly byte[] SegraBoxId = "SEGRA-METADATA\0\u0001"u8.ToArray();
        private static readonly byte[] ChapterDataMagic = "SEGRACHP"u8.ToArray();
        private static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "minf", "stbl", "udta", "edts", "dinf", "tref"];
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);
        private static readonly SemaphoreSlim _writeLock = new(1, 1);
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();

        // Fixed parts of a QuickTime text chapter track, as written by ffmpeg
        private static readonly byte[] ChapterHdlr = Convert.FromHexString("0000000000000000746578740000000000000000000000005375627469746c6548616e646c657200");
        private static readonly byte[] ChapterGmhd = Convert.FromHexString("00000018676d696e000000000040800080008000000000000000002c74657874000100000000000000000000000000000001000000000000000000000000000040000000");
        private static readonly byte[] ChapterDinf = Convert.FromHexString("0000001c6472656600000000000000010000000c75726c2000000001");
        private static readonly byte[] ChapterStsd = Convert.FromHexString("00000000000000010000004f7465787400000000000000010000000100000000000000000000000000000000000000010000000000000000000d6674616200010001000000001462747274000000000000000100000001");

        private sealed class Box
        {
            public string Type;
            public byte[]? Payload;
            public List<Box>? Children;

            public Box(string type, byte[] payload) { Type = type; Payload = payload; }
            public Box(string type, List<Box> children) { Type = type; Children = children; }
        }

        private readonly record struct TopBox(string Type, long Offset, long Size, int HeaderSize);

        /// <summary>
        /// Writes the content's current metadata into its file after a short delay, so a burst of edits is one write.
        /// </summary>
        public static void Schedule(string contentId)
        {
            var cts = new CancellationTokenSource();
            _pending.AddOrUpdate(contentId, cts, (_, old) => { old.Cancel(); return cts; });

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Debounce, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                _pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(contentId, cts));

                var content = AppState.Instance.Content.FirstOrDefault(c => c.Id == contentId);
                if (content != null)
                {
                    await EmbedAsync(content);
                }
            });
        }

        public static async Task EmbedAsync(Content content)
        {
            var metadata = new EmbeddedMetadata
            {
                Game = content.Game,
                IgdbId = content.IgdbId,
                GameExePath = content.GameExePath,
                Title = content.Title,
                CreatedAt = content.CreatedAt,
                AudioTrackNames = content.AudioTrackNames,
                AudioTrackTypes = content.AudioTrackTypes,
                Compressed = content.Compressed,
                Bookmarks = content.Bookmarks?.OrderBy(b => b.Time).ToList() ?? [],
            };

            using var work = BackgroundWork.Begin();
            await _writeLock.WaitAsync();
            try
            {
                await Task.Run(() => Embed(content.FilePath, metadata));
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not embed metadata in {content.FilePath}: {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Returns the metadata Segra embedded in the file, or null when it has none.
        /// </summary>
        public static Task<EmbeddedMetadata?> ReadAsync(string filePath) => Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var moov = ReadMoov(fs, out _);
                string? json = moov != null ? FindSegraJson(moov) : null;
                return json != null ? JsonSerializer.Deserialize<EmbeddedMetadata>(json) : null;
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not read embedded metadata from {filePath}: {ex.Message}");
                return null;
            }
        });

        private static void Embed(string filePath, EmbeddedMetadata metadata)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var moov = ReadMoov(fs, out var top);
            if (moov == null || top == null)
            {
                Log.Warning($"Could not embed metadata in {filePath}: unsupported MP4 layout");
                return;
            }

            var bookmarks = metadata.Bookmarks;
            string json = JsonSerializer.Serialize(metadata);
            if (FindSegraJson(moov) == json)
            {
                return;
            }

            var mvhd = moov.FirstOrDefault(b => b.Type == "mvhd")?.Payload;
            if (mvhd == null || mvhd.Length < 100)
            {
                return;
            }
            bool v1 = mvhd[0] == 1;
            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(v1 ? 20 : 12));
            ulong movieDuration = v1 ? BinaryPrimitives.ReadUInt64BigEndian(mvhd.AsSpan(24)) : BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(16));
            if (timescale == 0)
            {
                return;
            }

            uint? chapterTrackId = RemoveChapters(moov);
            var udta = moov.FirstOrDefault(b => b.Type == "udta" && b.Children != null);
            if (udta == null)
            {
                udta = new Box("udta", new List<Box>());
                moov.Add(udta);
            }

            Box? co64 = null;
            byte[] samples = [];
            udta.Children!.Add(new Box("uuid", [.. SegraBoxId, .. Encoding.UTF8.GetBytes(json)]));
            if (bookmarks.Count > 0)
            {
                if (chapterTrackId == null)
                {
                    chapterTrackId = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(mvhd.Length - 4));
                    BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(mvhd.Length - 4), chapterTrackId.Value + 1);
                }

                long totalMs = (long)(movieDuration * 1000 / timescale);
                var chapters = BuildChapters(bookmarks, totalMs);
                (co64, samples) = AddChapterTrack(moov, chapterTrackId.Value, (uint)Math.Min(movieDuration, uint.MaxValue), totalMs, chapters);
                udta.Children.Add(new Box("chpl", BuildChpl(chapters)));
            }

            (byte[] Data, int MoovOffset) BuildTail(long offset)
            {
                var tail = new List<byte>();
                if (co64 != null)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(co64.Payload.AsSpan(8), (ulong)(offset + 8 + ChapterDataMagic.Length));
                    tail.AddRange(Serialize(new Box("mdat", [.. ChapterDataMagic, .. samples])));
                }
                int moovOffset = tail.Count;
                tail.AddRange(Serialize(new Box("moov", moov)));
                return (tail.ToArray(), moovOffset);
            }

            WriteTail(fs, top, BuildTail);
            Log.Information($"Embedded metadata and {bookmarks.Count} bookmarks in {filePath}");
        }

        // Every step leaves exactly one complete moov first in the file, so an interrupted write never loses the index.
        private static void WriteTail(FileStream fs, List<TopBox> top, Func<long, (byte[] Data, int MoovOffset)> buildTail)
        {
            int moovIdx = top.FindIndex(b => b.Type == "moov");
            TopBox oldMoov = top[moovIdx];
            int tailSize = buildTail(0).Data.Length;

            int suffix = top.Count;
            while (suffix > 0 && IsTailBox(fs, top[suffix - 1]))
            {
                suffix--;
            }

            if (moovIdx >= suffix)
            {
                // Reuse the dead space left by earlier updates when the new index fits there
                int liveStart = moovIdx > suffix && IsOurMdat(fs, top[moovIdx - 1]) ? moovIdx - 1 : moovIdx;
                long deadStart = top[suffix].Offset;
                long deadEnd = top[liveStart].Offset;
                long spare = deadEnd - deadStart - tailSize;
                if (deadEnd - deadStart <= uint.MaxValue && (spare == 0 || spare >= 8))
                {
                    var (tail, moovOffset) = buildTail(deadStart);
                    "free"u8.CopyTo(tail.AsSpan(moovOffset + 4));

                    WriteAt(fs, deadStart, FreeHeader(deadEnd - deadStart));
                    fs.Flush(true);
                    WriteAt(fs, deadStart + 8, tail.AsSpan(8));
                    if (spare > 0)
                    {
                        WriteAt(fs, deadStart + tail.Length, FreeHeader(spare));
                    }
                    fs.Flush(true);
                    WriteAt(fs, deadStart, tail.AsSpan(0, 8));
                    fs.Flush(true);
                    WriteAt(fs, deadStart + moovOffset + 4, "moov"u8);
                    fs.Flush(true);
                    WriteAt(fs, oldMoov.Offset + 4, "free"u8);
                    fs.Flush(true);
                    fs.SetLength(deadEnd);
                    return;
                }
            }

            long end = fs.Length;
            WriteAt(fs, end, buildTail(end).Data);
            fs.Flush(true);
            WriteAt(fs, oldMoov.Offset + 4, "free"u8);
            fs.Flush(true);
        }

        private static List<Box>? ReadMoov(FileStream fs, out List<TopBox>? top)
        {
            top = ScanTopLevel(fs);
            if (top == null || top.Any(b => b.Type == "moof") || top.Count(b => b.Type == "moov") != 1)
            {
                return null;
            }

            TopBox moov = top.First(b => b.Type == "moov");
            long length = moov.Size - moov.HeaderSize;
            if (length > 256 * 1024 * 1024)
            {
                return null;
            }

            byte[] data = new byte[length];
            fs.Position = moov.Offset + moov.HeaderSize;
            fs.ReadExactly(data);
            return ParseChildren(data);
        }

        private static List<TopBox>? ScanTopLevel(FileStream fs)
        {
            var boxes = new List<TopBox>();
            long pos = 0;
            long length = fs.Length;
            Span<byte> header = stackalloc byte[16];
            while (pos < length)
            {
                if (length - pos < 8)
                {
                    return null;
                }
                fs.Position = pos;
                fs.ReadExactly(header[..8]);
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                int headerSize = 8;
                if (size == 1)
                {
                    fs.ReadExactly(header[8..]);
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                    headerSize = 16;
                }
                else if (size == 0)
                {
                    size = length - pos;
                }
                if (size < headerSize || size > length - pos)
                {
                    return null;
                }
                boxes.Add(new TopBox(Encoding.Latin1.GetString(header.Slice(4, 4)), pos, size, headerSize));
                pos += size;
            }
            return boxes;
        }

        private static bool IsTailBox(FileStream fs, TopBox box) =>
            box.Type is "free" or "skip" or "moov" || IsOurMdat(fs, box);

        private static bool IsOurMdat(FileStream fs, TopBox box)
        {
            if (box.Type != "mdat" || box.Size < box.HeaderSize + ChapterDataMagic.Length || box.Size > 1024 * 1024)
            {
                return false;
            }
            Span<byte> magic = stackalloc byte[ChapterDataMagic.Length];
            fs.Position = box.Offset + box.HeaderSize;
            fs.ReadExactly(magic);
            return magic.SequenceEqual(ChapterDataMagic);
        }

        // Returns null when the data isn't a clean list of boxes, so the caller keeps it as raw bytes
        private static List<Box>? ParseChildren(ReadOnlySpan<byte> data)
        {
            var boxes = new List<Box>();
            int pos = 0;
            while (pos < data.Length)
            {
                if (data.Length - pos < 8)
                {
                    return null;
                }
                uint size = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
                if (size < 8 || size > data.Length - pos)
                {
                    return null;
                }
                string type = Encoding.Latin1.GetString(data.Slice(pos + 4, 4));
                var payload = data.Slice(pos + 8, (int)size - 8);
                var children = Containers.Contains(type) ? ParseChildren(payload) : null;
                boxes.Add(children != null ? new Box(type, children) : new Box(type, payload.ToArray()));
                pos += (int)size;
            }
            return boxes;
        }

        private static byte[] Serialize(Box box)
        {
            byte[] payload = box.Children != null
                ? box.Children.SelectMany(Serialize).ToArray()
                : box.Payload!;
            byte[] data = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(data, (uint)data.Length);
            Encoding.Latin1.GetBytes(box.Type, data.AsSpan(4, 4));
            payload.CopyTo(data, 8);
            return data;
        }

        private static string? FindSegraJson(List<Box> moov)
        {
            var box = moov.FirstOrDefault(b => b.Type == "udta")?.Children?.FirstOrDefault(IsSegraBox);
            return box != null ? Encoding.UTF8.GetString(box.Payload.AsSpan(SegraBoxId.Length)) : null;
        }

        private static bool IsSegraBox(Box box) =>
            box.Type == "uuid" && box.Payload != null && box.Payload.AsSpan().StartsWith(SegraBoxId);

        // Removes chapter tracks, their references and chapter lists, and returns the freed track id for reuse
        private static uint? RemoveChapters(List<Box> moov)
        {
            var ids = new HashSet<uint>();
            foreach (var trak in moov.Where(b => b.Type == "trak" && b.Children != null))
            {
                var tref = trak.Children!.FirstOrDefault(b => b.Type == "tref" && b.Children != null);
                if (tref == null)
                {
                    continue;
                }
                foreach (var chap in tref.Children!.Where(b => b.Type == "chap"))
                {
                    for (int i = 0; i + 4 <= chap.Payload!.Length; i += 4)
                    {
                        ids.Add(BinaryPrimitives.ReadUInt32BigEndian(chap.Payload.AsSpan(i)));
                    }
                }
                tref.Children!.RemoveAll(b => b.Type == "chap");
                if (tref.Children.Count == 0)
                {
                    trak.Children!.Remove(tref);
                }
            }

            moov.RemoveAll(b => b.Type == "trak" && ids.Contains(TrackId(b)));
            moov.FirstOrDefault(b => b.Type == "udta")?.Children?.RemoveAll(b => b.Type == "chpl" || IsSegraBox(b));
            return ids.Count > 0 ? ids.Min() : null;
        }

        private static uint TrackId(Box trak)
        {
            var tkhd = trak.Children?.FirstOrDefault(b => b.Type == "tkhd")?.Payload;
            if (tkhd == null || tkhd.Length < 24)
            {
                return 0;
            }
            return BinaryPrimitives.ReadUInt32BigEndian(tkhd.AsSpan(tkhd[0] == 1 ? 20 : 12));
        }

        private static List<(long Ms, string Title)> BuildChapters(List<Bookmark> bookmarks, long totalMs)
        {
            var chapters = new List<(long Ms, string Title)>();
            foreach (var bookmark in bookmarks)
            {
                long ms = Math.Clamp((long)bookmark.Time.TotalMilliseconds, 0, Math.Max(0, totalMs - 1));
                if (chapters.Count > 0 && chapters[^1].Ms >= ms)
                {
                    continue;
                }
                // Chapters must start at 0 or players shift the first one there
                if (chapters.Count == 0 && ms > 0)
                {
                    chapters.Add((0, "Start"));
                }
                chapters.Add((ms, GetChapterName(bookmark)));
            }
            return chapters;
        }

        private static string GetChapterName(Bookmark bookmark)
        {
            string name = bookmark.Type == BookmarkType.Manual ? "Bookmark" : bookmark.Type.ToString();
            return bookmark.Subtype != null ? $"{name} ({bookmark.Subtype})" : name;
        }

        private static (Box Co64, byte[] Samples) AddChapterTrack(List<Box> moov, uint trackId, uint movieDuration, long totalMs, List<(long Ms, string Title)> chapters)
        {
            var samples = chapters.Select(c => ChapterSample(c.Title)).ToList();
            var stts = new BigEndianWriter().U32(0).U32((uint)chapters.Count);
            for (int i = 0; i < chapters.Count; i++)
            {
                long next = i + 1 < chapters.Count ? chapters[i + 1].Ms : totalMs;
                stts.U32(1).U32((uint)Math.Max(1, next - chapters[i].Ms));
            }
            var stsz = new BigEndianWriter().U32(0).U32(0).U32((uint)samples.Count);
            samples.ForEach(s => stsz.U32((uint)s.Length));
            var co64 = new Box("co64", new BigEndianWriter().U32(0).U32(1).U64(0).ToArray());

            byte[] matrix = new BigEndianWriter().U32(0x10000).U32(0).U32(0).U32(0).U32(0x10000).U32(0).U32(0).U32(0).U32(0x40000000).ToArray();
            var trak = new Box("trak", new List<Box>
            {
                // Flags 2: part of the movie but not enabled, so players don't render it as a text track
                new("tkhd", new BigEndianWriter().U32(2).U32(0).U32(0).U32(trackId).U32(0).U32(movieDuration).U64(0).U32(0).U32(0).Bytes(matrix).U32(0).U32(0).ToArray()),
                new("edts", new List<Box> { new("elst", new BigEndianWriter().U32(0).U32(1).U32(movieDuration).U32(0).U32(0x10000).ToArray()) }),
                new("mdia", new List<Box>
                {
                    new("mdhd", new BigEndianWriter().U32(0).U32(0).U32(0).U32(1000).U32((uint)totalMs).U16(0x55C4).U16(0).ToArray()),
                    new("hdlr", ChapterHdlr),
                    new("minf", new List<Box>
                    {
                        new("gmhd", ChapterGmhd),
                        new("dinf", ChapterDinf),
                        new("stbl", new List<Box>
                        {
                            new("stsd", ChapterStsd),
                            new("stts", stts.ToArray()),
                            new("stsc", new BigEndianWriter().U32(0).U32(1).U32(1).U32((uint)samples.Count).U32(1).ToArray()),
                            new("stsz", stsz.ToArray()),
                            co64,
                        }),
                    }),
                }),
            });

            foreach (var other in moov.Where(b => b.Type == "trak" && b.Children != null))
            {
                int mdia = other.Children!.FindIndex(b => b.Type == "mdia");
                other.Children.Insert(mdia < 0 ? other.Children.Count : mdia,
                    new Box("tref", new List<Box> { new("chap", new BigEndianWriter().U32(trackId).ToArray()) }));
            }
            moov.Insert(moov.FindLastIndex(b => b.Type == "trak") + 1, trak);
            return (co64, samples.SelectMany(s => s).ToArray());
        }

        private static byte[] ChapterSample(string title)
        {
            byte[] text = Encoding.UTF8.GetBytes(title);
            // encd marks the text as UTF-8
            return new BigEndianWriter().U16((ushort)text.Length).Bytes(text).U32(12).Bytes("encd"u8).U32(0x100).ToArray();
        }

        // Nero chapter list, read by players that ignore QuickTime chapter tracks
        private static byte[] BuildChpl(List<(long Ms, string Title)> chapters)
        {
            var chpl = new BigEndianWriter().U32(0x01000000).U32(0).U8((byte)Math.Min(chapters.Count, 255));
            foreach (var (ms, title) in chapters.Take(255))
            {
                byte[] text = Encoding.UTF8.GetBytes(title);
                int length = Math.Min(text.Length, 255);
                chpl.U64((ulong)ms * 10000).U8((byte)length).Bytes(text.AsSpan(0, length));
            }
            return chpl.ToArray();
        }

        private static byte[] FreeHeader(long size)
        {
            return new BigEndianWriter().U32((uint)size).Bytes("free"u8).ToArray();
        }

        private static void WriteAt(FileStream fs, long offset, ReadOnlySpan<byte> data)
        {
            fs.Position = offset;
            fs.Write(data);
        }

        private sealed class BigEndianWriter
        {
            private readonly MemoryStream _stream = new();

            public BigEndianWriter U8(byte value) { _stream.WriteByte(value); return this; }
            public BigEndianWriter U16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); _stream.Write(b); return this; }
            public BigEndianWriter U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); _stream.Write(b); return this; }
            public BigEndianWriter U64(ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); _stream.Write(b); return this; }
            public BigEndianWriter Bytes(ReadOnlySpan<byte> value) { _stream.Write(value); return this; }
            public byte[] ToArray() => _stream.ToArray();
        }
    }

    internal sealed record EmbeddedMetadata
    {
        public int Version { get; init; } = 1;
        public string? Game { get; init; }
        public int? IgdbId { get; init; }
        public string? GameExePath { get; init; }
        public string? Title { get; init; }
        public DateTime? CreatedAt { get; init; }
        public List<string>? AudioTrackNames { get; init; }
        public List<string>? AudioTrackTypes { get; init; }
        public bool Compressed { get; init; }
        public List<Bookmark> Bookmarks { get; init; } = [];
    }
}
