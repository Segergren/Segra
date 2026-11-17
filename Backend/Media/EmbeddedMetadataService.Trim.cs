using Serilog;
using System.Buffers.Binary;
using System.Text;

namespace Segra.Backend.Media
{
    // Trims a recording by rewriting only its index. The frames stay where they are: the ones after the cut are
    // dropped by shortening the file, the ones before it stay unused until SpaceReclaimService rewrites the file.
    internal static partial class EmbeddedMetadataService
    {
        /// <param name="Start">Where the trimmed video starts in the original, at the keyframe at or before the requested start</param>
        /// <param name="UnusedBytes">Bytes before the cut that nothing points to anymore</param>
        public sealed record IndexTrim(TimeSpan Start, long UnusedBytes);

        private static readonly HashSet<string> TrimmableStblBoxes = ["stsd", "stts", "ctts", "stss", "stsz", "stsc", "stco", "co64", "sdtp", "sbgp", "sgpd", "cslg"];

        /// <summary>
        /// Trims the video to [from, to] without copying it. Returns null when the file can't be trimmed this way,
        /// e.g. an unusual layout, and leaves it unchanged then.
        /// </summary>
        public static Task<IndexTrim?> TrimIndexAsync(string filePath, TimeSpan from, TimeSpan to) =>
            RunExclusiveAsync(() => Task.Run(() =>
            {
                try
                {
                    return TrimIndex(filePath, from.TotalSeconds, to.TotalSeconds);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not trim the index of {filePath}: {ex.Message}");
                    return null;
                }
            }));

        private sealed class Track
        {
            public required Box Trak;
            public required Box Stbl;
            public required string Handler;
            public uint Timescale;
            public long[] Dts = [];
            public uint[] Durations = [];
            public int[]? Offsets; // ctts
            public byte CttsVersion;
            public uint[] Sizes = [];
            public bool UniformSize;
            public HashSet<int>? Keyframes; // null when every sample is a keyframe
            public long[] FilePos = [];
            public int[] Chunk = [];
            public uint[] Sdi = [];
            public byte[]? Sdtp;
            public uint[]? Groups; // sbgp
            public byte[]? SbgpHeader;
            public long MediaTime; // edit list start
            public double EmptySeconds; // leading empty edit
            public int First, Last; // kept samples
            public long NewMediaTime;
            public ulong NewSegment;

            public double Pts(int i) => (Dts[i] + (Offsets?[i] ?? 0) - MediaTime) / (double)Timescale + EmptySeconds;
            public double PtsEnd(int i) => Pts(i) + Durations[i] / (double)Timescale;
        }

        private static IndexTrim? TrimIndex(string filePath, double from, double to)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var moov = ReadMoov(fs, out var top);
            if (moov == null || top == null)
            {
                return null;
            }
            int moovIdx = top.FindIndex(b => b.Type == "moov");
            int mediaIdx = MediaIndex(fs, top);
            if (mediaIdx < 0 || moovIdx < mediaIdx)
            {
                return null;
            }

            // Chapters and Segra's metadata are written again when the session is saved
            RemoveChapters(moov);
            var mvhd = moov.FirstOrDefault(b => b.Type == "mvhd")?.Payload;
            if (mvhd == null || mvhd.Length < 100)
            {
                return null;
            }
            bool mvhdV1 = mvhd[0] == 1;
            uint movieTimescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(mvhdV1 ? 20 : 12));
            if (movieTimescale == 0)
            {
                return null;
            }

            var tracks = new List<Track>();
            foreach (var trak in moov.Where(b => b.Type == "trak"))
            {
                var track = ReadTrack(trak, movieTimescale);
                if (track == null)
                {
                    return null;
                }
                tracks.Add(track);
            }
            var video = tracks.FirstOrDefault(t => t.Handler == "vide");
            if (video == null || tracks.Any(t => t.Handler is not ("vide" or "soun")) || tracks.Any(t => t.Dts.Length == 0))
            {
                return null;
            }

            // Video has to start on a keyframe, the frames after it only store changes
            int key = -1;
            for (int i = 0; i < video.Dts.Length; i++)
            {
                if (video.Keyframes != null && !video.Keyframes.Contains(i))
                {
                    continue;
                }
                if (key < 0 || video.Pts(i) <= from)
                {
                    key = i;
                }
                if (video.Pts(i) > from)
                {
                    break;
                }
            }
            double cut = Math.Max(0, video.Pts(key));

            foreach (var track in tracks)
            {
                int first = track == video ? key : 0;
                while (track != video && first < track.Dts.Length - 1 && track.PtsEnd(first) <= cut)
                {
                    first++;
                }
                int last = first;
                for (int i = first; i < track.Dts.Length; i++)
                {
                    if (track.Pts(i) < to)
                    {
                        last = i;
                    }
                }
                track.First = first;
                track.Last = last;
            }

            if (key == 0 && tracks.All(t => t.Last == t.Dts.Length - 1))
            {
                return new IndexTrim(TimeSpan.Zero, 0);
            }

            double length = 0;
            foreach (var track in tracks)
            {
                double end = Math.Min(to, Enumerable.Range(track.First, track.Last - track.First + 1).Max(track.PtsEnd));
                track.NewMediaTime = Math.Max(0, (long)Math.Round(track.MediaTime - track.Dts[track.First] + cut * track.Timescale - track.EmptySeconds * track.Timescale));
                track.NewSegment = (ulong)Math.Max(0, Math.Round((end - cut) * movieTimescale));
                length = Math.Max(length, end - cut);
                WriteTrack(track);
            }
            WriteDuration(mvhd, mvhdV1 ? 24 : 16, mvhdV1, (ulong)Math.Round(length * movieTimescale));

            long mediaStart = top[mediaIdx].Offset + top[mediaIdx].HeaderSize;
            long keptStart = tracks.Min(t => Enumerable.Range(t.First, t.Last - t.First + 1).Min(i => t.FilePos[i]));
            long keptEnd = tracks.Max(t => Enumerable.Range(t.First, t.Last - t.First + 1).Max(i => t.FilePos[i] + t.Sizes[i]));
            WriteIndex(fs, top, moovIdx, Serialize(new Box("moov", moov)), keptEnd);

            Log.Information($"Trimmed the index of {filePath} to {TimeSpan.FromSeconds(cut):hh\\:mm\\:ss\\.ff} - {TimeSpan.FromSeconds(cut + length):hh\\:mm\\:ss\\.ff}, {(keptStart - mediaStart) / 1048576} MB before the cut unused");
            return new IndexTrim(TimeSpan.FromSeconds(cut), Math.Max(0, keptStart - mediaStart));
        }

        private static Track? ReadTrack(Box trak, uint movieTimescale)
        {
            var mdia = trak.Children?.FirstOrDefault(b => b.Type == "mdia");
            var mdhd = mdia?.Children?.FirstOrDefault(b => b.Type == "mdhd")?.Payload;
            var hdlr = mdia?.Children?.FirstOrDefault(b => b.Type == "hdlr")?.Payload;
            var stbl = mdia?.Children?.FirstOrDefault(b => b.Type == "minf")?.Children?.FirstOrDefault(b => b.Type == "stbl");
            if (mdhd == null || hdlr == null || hdlr.Length < 12 || stbl?.Children == null || stbl.Children.Any(b => !TrimmableStblBoxes.Contains(b.Type)))
            {
                return null;
            }

            var track = new Track
            {
                Trak = trak,
                Stbl = stbl,
                Handler = Encoding.Latin1.GetString(hdlr, 8, 4),
                Timescale = BinaryPrimitives.ReadUInt32BigEndian(mdhd.AsSpan(mdhd[0] == 1 ? 20 : 12)),
            };
            if (track.Timescale == 0)
            {
                return null;
            }
            byte[]? Table(string type) => stbl.Children.FirstOrDefault(b => b.Type == type)?.Payload;
            static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));

            // Sample sizes
            var stsz = Table("stsz");
            if (stsz == null)
            {
                return null;
            }
            uint uniform = U32(stsz, 4);
            int count = (int)U32(stsz, 8);
            track.UniformSize = uniform != 0;
            track.Sizes = new uint[count];
            for (int i = 0; i < count; i++)
            {
                track.Sizes[i] = uniform != 0 ? uniform : U32(stsz, 12 + i * 4);
            }

            // Decode times
            var stts = Table("stts");
            if (stts == null)
            {
                return null;
            }
            track.Dts = new long[count];
            track.Durations = new uint[count];
            int s = 0;
            long dts = 0;
            for (int e = 0, entries = (int)U32(stts, 4); e < entries; e++)
            {
                uint n = U32(stts, 8 + e * 8), delta = U32(stts, 12 + e * 8);
                for (uint k = 0; k < n && s < count; k++, s++)
                {
                    track.Dts[s] = dts;
                    track.Durations[s] = delta;
                    dts += delta;
                }
            }
            if (s != count)
            {
                return null;
            }

            // Composition offsets
            if (Table("ctts") is byte[] ctts)
            {
                track.CttsVersion = ctts[0];
                track.Offsets = new int[count];
                s = 0;
                for (int e = 0, entries = (int)U32(ctts, 4); e < entries; e++)
                {
                    uint n = U32(ctts, 8 + e * 8);
                    int offset = ctts[0] == 1 ? BinaryPrimitives.ReadInt32BigEndian(ctts.AsSpan(12 + e * 8)) : (int)U32(ctts, 12 + e * 8);
                    for (uint k = 0; k < n && s < count; k++, s++)
                    {
                        track.Offsets[s] = offset;
                    }
                }
            }

            if (Table("stss") is byte[] stss)
            {
                track.Keyframes = [];
                for (int e = 0, entries = (int)U32(stss, 4); e < entries; e++)
                {
                    track.Keyframes.Add((int)U32(stss, 8 + e * 4) - 1);
                }
            }

            // Where each sample is in the file
            var stsc = Table("stsc");
            var co64 = Table("co64");
            var stco = Table("stco");
            if (stsc == null || (co64 == null) == (stco == null))
            {
                return null;
            }
            int chunks = (int)U32(co64 ?? stco!, 4);
            long ChunkOffset(int c) => co64 != null ? (long)BinaryPrimitives.ReadUInt64BigEndian(co64.AsSpan(8 + c * 8)) : U32(stco!, 8 + c * 4);
            track.FilePos = new long[count];
            track.Chunk = new int[count];
            track.Sdi = new uint[count];
            int stscEntries = (int)U32(stsc, 4);
            s = 0;
            for (int e = 0; e < stscEntries; e++)
            {
                int firstChunk = (int)U32(stsc, 8 + e * 12) - 1;
                int nextChunk = e + 1 < stscEntries ? (int)U32(stsc, 8 + (e + 1) * 12) - 1 : chunks;
                uint perChunk = U32(stsc, 12 + e * 12), sdi = U32(stsc, 16 + e * 12);
                for (int c = firstChunk; c < nextChunk; c++)
                {
                    long pos = ChunkOffset(c);
                    for (uint k = 0; k < perChunk && s < count; k++, s++)
                    {
                        track.FilePos[s] = pos;
                        track.Chunk[s] = c;
                        track.Sdi[s] = sdi;
                        pos += track.Sizes[s];
                    }
                }
            }
            if (s != count)
            {
                return null;
            }

            if (Table("sdtp") is byte[] sdtp)
            {
                if (sdtp.Length != 4 + count)
                {
                    return null;
                }
                track.Sdtp = sdtp;
            }

            if (Table("sbgp") is byte[] sbgp)
            {
                if (stbl.Children.Count(b => b.Type == "sbgp") > 1)
                {
                    return null;
                }
                int header = sbgp[0] == 1 ? 12 : 8;
                track.SbgpHeader = sbgp[..header];
                track.Groups = new uint[count];
                s = 0;
                for (int e = 0, entries = (int)U32(sbgp, header); e < entries; e++)
                {
                    uint n = U32(sbgp, header + 4 + e * 8), group = U32(sbgp, header + 8 + e * 8);
                    for (uint k = 0; k < n && s < count; k++, s++)
                    {
                        track.Groups[s] = group;
                    }
                }
            }

            // An optional empty edit, then one edit into the media
            var elst = trak.Children!.FirstOrDefault(b => b.Type == "edts")?.Children?.FirstOrDefault(b => b.Type == "elst")?.Payload;
            if (elst != null)
            {
                bool v1 = elst[0] == 1;
                int entries = (int)U32(elst, 4);
                int size = v1 ? 20 : 12;
                ulong empty = 0;
                int media = 0;
                for (int e = 0; e < entries; e++)
                {
                    int o = 8 + e * size;
                    ulong segment = v1 ? BinaryPrimitives.ReadUInt64BigEndian(elst.AsSpan(o)) : U32(elst, o);
                    long time = v1 ? BinaryPrimitives.ReadInt64BigEndian(elst.AsSpan(o + 8)) : BinaryPrimitives.ReadInt32BigEndian(elst.AsSpan(o + 4));
                    if (time == -1)
                    {
                        empty += segment;
                        continue;
                    }
                    track.MediaTime = time;
                    media++;
                }
                if (media > 1)
                {
                    return null;
                }
                track.EmptySeconds = empty / (double)movieTimescale;
            }
            return track;
        }

        private static void WriteTrack(Track t)
        {
            int count = t.Last - t.First + 1;
            IEnumerable<int> kept = Enumerable.Range(t.First, count);

            var stts = new List<(uint N, uint Value)>();
            var ctts = new List<(uint N, uint Value)>();
            var groups = new List<(uint N, uint Value)>();
            static void Add(List<(uint N, uint Value)> runs, uint value)
            {
                if (runs.Count > 0 && runs[^1].Value == value)
                    runs[^1] = (runs[^1].N + 1, value);
                else
                    runs.Add((1, value));
            }
            var chunkOffsets = new List<long>();
            var stsc = new List<(uint FirstChunk, uint PerChunk, uint Sdi)>();
            uint inChunk = 0;
            foreach (int i in kept)
            {
                Add(stts, t.Durations[i]);
                if (t.Offsets != null)
                    Add(ctts, (uint)t.Offsets[i]);
                if (t.Groups != null)
                    Add(groups, t.Groups[i]);

                // A new chunk wherever the original one changes, since samples within a chunk are contiguous
                if (i == t.First || t.Chunk[i] != t.Chunk[i - 1])
                {
                    if (chunkOffsets.Count > 0)
                        AddChunkRun(stsc, (uint)chunkOffsets.Count, inChunk, t.Sdi[i - 1]);
                    chunkOffsets.Add(t.FilePos[i]);
                    inChunk = 0;
                }
                inChunk++;
            }
            AddChunkRun(stsc, (uint)chunkOffsets.Count, inChunk, t.Sdi[t.Last]);

            var tables = new Dictionary<string, byte[]>
            {
                ["stts"] = Runs(stts, 0),
                ["stsc"] = BuildStsc(stsc),
                ["co64"] = BuildCo64(chunkOffsets),
            };
            if (t.Offsets != null)
                tables["ctts"] = Runs(ctts, t.CttsVersion);
            if (t.Keyframes != null)
            {
                var keys = kept.Where(t.Keyframes.Contains).Select(i => (uint)(i - t.First + 1)).ToList();
                var stss = new BigEndianWriter().U32(0).U32((uint)keys.Count);
                keys.ForEach(k => stss.U32(k));
                tables["stss"] = stss.ToArray();
            }
            var stsz = new BigEndianWriter().U32(0).U32(t.UniformSize ? t.Sizes[t.First] : 0).U32((uint)count);
            if (!t.UniformSize)
                foreach (int i in kept)
                    stsz.U32(t.Sizes[i]);
            tables["stsz"] = stsz.ToArray();
            if (t.Sdtp != null)
                tables["sdtp"] = [.. t.Sdtp[..4], .. t.Sdtp.AsSpan(4 + t.First, count)];
            if (t.Groups != null)
            {
                var sbgp = new BigEndianWriter().Bytes(t.SbgpHeader!).U32((uint)groups.Count);
                groups.ForEach(g => sbgp.U32(g.N).U32(g.Value));
                tables["sbgp"] = sbgp.ToArray();
            }

            var children = t.Stbl.Children!;
            children.RemoveAll(b => b.Type is "stco" or "cslg");
            foreach (var (type, payload) in tables)
            {
                var box = children.FirstOrDefault(b => b.Type == type);
                if (box != null)
                    box.Payload = payload;
                else
                    children.Add(new Box(type, payload));
            }

            // Durations and the edit list
            var mdia = t.Trak.Children!.First(b => b.Type == "mdia");
            var mdhd = mdia.Children!.First(b => b.Type == "mdhd").Payload!;
            WriteDuration(mdhd, mdhd[0] == 1 ? 24 : 16, mdhd[0] == 1, (ulong)kept.Sum(i => (long)t.Durations[i]));
            var tkhd = t.Trak.Children!.First(b => b.Type == "tkhd").Payload!;
            WriteDuration(tkhd, tkhd[0] == 1 ? 28 : 20, tkhd[0] == 1, t.NewSegment);

            var elst = new BigEndianWriter().U32(0x01000000).U32(1).U64(t.NewSegment).U64((ulong)t.NewMediaTime).U32(0x00010000).ToArray();
            t.Trak.Children!.RemoveAll(b => b.Type == "edts");
            int tkhdIndex = t.Trak.Children.FindIndex(b => b.Type == "tkhd");
            t.Trak.Children.Insert(tkhdIndex + 1, new Box("edts", [new Box("elst", elst)]));
        }

        private static void AddChunkRun(List<(uint FirstChunk, uint PerChunk, uint Sdi)> stsc, uint chunk, uint perChunk, uint sdi)
        {
            if (stsc.Count == 0 || stsc[^1].PerChunk != perChunk || stsc[^1].Sdi != sdi)
                stsc.Add((chunk, perChunk, sdi));
        }

        private static byte[] Runs(List<(uint N, uint Value)> runs, byte version)
        {
            var w = new BigEndianWriter().U32((uint)version << 24).U32((uint)runs.Count);
            runs.ForEach(r => w.U32(r.N).U32(r.Value));
            return w.ToArray();
        }

        private static byte[] BuildStsc(List<(uint FirstChunk, uint PerChunk, uint Sdi)> stsc)
        {
            var w = new BigEndianWriter().U32(0).U32((uint)stsc.Count);
            stsc.ForEach(e => w.U32(e.FirstChunk).U32(e.PerChunk).U32(e.Sdi));
            return w.ToArray();
        }

        private static byte[] BuildCo64(List<long> offsets)
        {
            var w = new BigEndianWriter().U32(0).U32((uint)offsets.Count);
            offsets.ForEach(o => w.U64((ulong)o));
            return w.ToArray();
        }

        private static void WriteDuration(byte[] payload, int offset, bool v1, ulong duration)
        {
            if (v1)
                BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(offset), duration);
            else
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset), (uint)Math.Min(duration, uint.MaxValue));
        }

        // Puts the new index right after the last kept frame and cuts the file there. Every step leaves one
        // complete index in the file, so an interrupted write never loses it.
        private static void WriteIndex(FileStream fs, List<TopBox> top, int moovIdx, byte[] moov, long keptEnd)
        {
            TopBox oldMoov = top[moovIdx];
            int last = top.FindLastIndex(moovIdx, b => b.Offset < keptEnd);
            TopBox media = top[last];
            if (media.Type == "mdat" && keptEnd + moov.Length + 16 <= oldMoov.Offset)
            {
                // Written inside the old media data first, where nothing reads it yet
                WriteAt(fs, keptEnd, moov);
                long rest = fs.Length - (keptEnd + moov.Length);
                WriteAt(fs, keptEnd + moov.Length, new BigEndianWriter().U32(1).Bytes("free"u8).U64((ulong)rest).ToArray());
                fs.Flush(true);
                long mediaSize = keptEnd - media.Offset;
                if (media.HeaderSize == 16)
                    WriteAt(fs, media.Offset + 8, new BigEndianWriter().U64((ulong)mediaSize).ToArray());
                else
                    WriteAt(fs, media.Offset, new BigEndianWriter().U32((uint)mediaSize).ToArray());
                fs.Flush(true);
                fs.SetLength(keptEnd + moov.Length);
                fs.Flush(true);
                return;
            }

            // Too little was cut at the end to fit the index, so it goes after the old one
            WriteAt(fs, fs.Length, moov);
            fs.Flush(true);
            WriteAt(fs, oldMoov.Offset + 4, "free"u8);
            fs.Flush(true);
        }
    }
}
