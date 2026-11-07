using System.Buffers.Binary;
using System.Text;

namespace Segra.Backend.Media
{
    // A normal MP4 index for a Hybrid MP4 that is still being written. It is served as a virtual
    // file: ftyp + rebuilt moov + mdat header, followed by the recording's own bytes, so nothing is copied.
    internal sealed class LiveMp4
    {
        public required string SourcePath { get; init; }
        public required byte[] Header { get; init; }
        public long DataStart { get; init; }
        public long DataLength { get; init; }
        public TimeSpan Duration { get; init; }
        public long Length => Header.Length + DataLength;

        private static readonly object _lock = new();
        private static LiveMp4? _current;

        public static LiveMp4? Current
        {
            get { lock (_lock) return _current; }
            set { lock (_lock) _current = value; }
        }

        public static LiveMp4? Find(string path)
        {
            var current = Current;
            return current != null && SamePath(current.SourcePath, path) ? current : null;
        }

        public static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        // Copies [position, position + count) of the virtual file into the buffer
        public int Read(FileStream source, long position, byte[] buffer, int count)
        {
            int written = 0;
            if (position < Header.Length)
            {
                int n = (int)Math.Min(count, Header.Length - position);
                Array.Copy(Header, position, buffer, 0, n);
                written = n;
                position += n;
            }
            if (written < count && position < Length)
            {
                source.Position = DataStart + (position - Header.Length);
                int n = (int)Math.Min(count - written, Length - position);
                source.ReadExactly(buffer, written, n);
                written += n;
            }
            return written;
        }

        private sealed class Box
        {
            public string Type = "";
            public byte[]? Data;
            public List<Box>? Children;

            public Box? Child(string type) => Children?.FirstOrDefault(c => c.Type == type);
        }

        private sealed class Track
        {
            public uint Id;
            public uint Timescale;
            public bool IsVideo;
            public bool IsAudio;
            public uint TrexDuration, TrexSize, TrexFlags;
            public readonly List<uint> Sizes = new();
            public readonly List<uint> Durations = new();
            public readonly List<int> Cts = new();
            public readonly List<uint> SyncSamples = new();
            public readonly List<(long Offset, uint Count)> Chunks = new();
            public readonly List<(int First, int Count, ulong Tfdt, bool MissingDurations)> Fragments = new();
            public ulong TotalDuration;
        }

        private static readonly HashSet<string> _containers = new() { "moov", "trak", "mdia", "minf", "stbl", "edts", "mvex", "moof", "traf" };

        public static LiveMp4 Build(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long fileLength = fs.Length;
            long pos = 0;
            byte[] header = new byte[16];
            Box? moov = null;
            Dictionary<uint, Track>? tracks = null;
            long dataStart = -1, dataEnd = -1;

            while (TryReadBoxHeader(fs, pos, fileLength, header, out long size, out string type, out _))
            {
                if (type == "moov")
                {
                    byte[] bytes = ReadAt(fs, pos, size);
                    moov = ParseBoxes(bytes, 0, bytes.Length)[0];
                    tracks = ReadTracks(moov);
                }
                else if (type == "moof" && tracks != null)
                {
                    // Only fragments whose mdat is fully on disk
                    long mdatPos = pos + size;
                    if (!TryReadBoxHeader(fs, mdatPos, fileLength, header, out long mdatSize, out string mdatType, out _) || mdatType != "mdat")
                        break;

                    byte[] moofBytes = ReadAt(fs, pos, size);
                    ParseMoof(ParseBoxes(moofBytes, 0, moofBytes.Length)[0], pos, tracks);
                    if (dataStart < 0) dataStart = pos;
                    dataEnd = mdatPos + mdatSize;
                    pos = dataEnd;
                    continue;
                }
                pos += size;
            }

            if (moov == null || tracks == null || dataStart < 0)
                throw new InvalidDataException("The recording has no complete fragments yet");

            foreach (var track in tracks.Values)
                FillMissingDurations(track);

            uint movieTimescale = ReadMovieTimescale(moov);
            byte[] ftyp = BuildFtyp();
            long dataLength = dataEnd - dataStart;

            // co64 entries are fixed size, so the moov length does not depend on the offsets
            byte[] probe = Serialize(BuildMoov(moov, tracks, movieTimescale, 0, dataStart));
            long virtualDataStart = ftyp.Length + probe.Length + 16;
            byte[] moovBytes = Serialize(BuildMoov(moov, tracks, movieTimescale, virtualDataStart, dataStart));

            byte[] mdatHeader = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(mdatHeader, 1);
            Encoding.ASCII.GetBytes("mdat").CopyTo(mdatHeader, 4);
            BinaryPrimitives.WriteUInt64BigEndian(mdatHeader.AsSpan(8), (ulong)(16 + dataLength));

            var video = tracks.Values.FirstOrDefault(t => t.IsVideo) ?? tracks.Values.First();
            return new LiveMp4
            {
                SourcePath = path,
                Header = [.. ftyp, .. moovBytes, .. mdatHeader],
                DataStart = dataStart,
                DataLength = dataLength,
                Duration = TimeSpan.FromSeconds(video.TotalDuration / (double)Math.Max(1u, video.Timescale)),
            };
        }

        private static bool TryReadBoxHeader(FileStream fs, long pos, long fileLength, byte[] buf, out long size, out string type, out int headerLength)
        {
            size = 0;
            type = "";
            headerLength = 8;
            if (pos + 8 > fileLength) return false;
            fs.Position = pos;
            fs.ReadExactly(buf, 0, 8);
            size = BinaryPrimitives.ReadUInt32BigEndian(buf);
            type = Encoding.ASCII.GetString(buf, 4, 4);
            if (size == 1)
            {
                if (pos + 16 > fileLength) return false;
                fs.ReadExactly(buf, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(8));
                headerLength = 16;
            }
            // size 0 means "to end of file", which is a box still being written
            return size >= headerLength && pos + size <= fileLength;
        }

        private static byte[] ReadAt(FileStream fs, long pos, long size)
        {
            byte[] bytes = new byte[size];
            fs.Position = pos;
            fs.ReadExactly(bytes);
            return bytes;
        }

        private static List<Box> ParseBoxes(byte[] buf, int start, int end)
        {
            var boxes = new List<Box>();
            int pos = start;
            while (pos + 8 <= end)
            {
                long size = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos));
                string type = Encoding.ASCII.GetString(buf, pos + 4, 4);
                int headerLength = 8;
                if (size == 1)
                {
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(pos + 8));
                    headerLength = 16;
                }
                else if (size == 0)
                {
                    size = end - pos;
                }
                if (size < headerLength || pos + size > end) break;

                var box = new Box { Type = type };
                if (_containers.Contains(type))
                    box.Children = ParseBoxes(buf, pos + headerLength, (int)(pos + size));
                else
                    box.Data = buf.AsSpan(pos + headerLength, (int)size - headerLength).ToArray();
                boxes.Add(box);
                pos += (int)size;
            }
            return boxes;
        }

        private static byte[] Serialize(Box box)
        {
            byte[] payload = box.Children != null
                ? box.Children.SelectMany(Serialize).ToArray()
                : box.Data ?? [];
            byte[] bytes = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
            Encoding.ASCII.GetBytes(box.Type).CopyTo(bytes, 4);
            payload.CopyTo(bytes, 8);
            return bytes;
        }

        private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
        private static ulong U64(byte[] d, int o) => BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(o));

        private static uint ReadMovieTimescale(Box moov)
        {
            byte[] mvhd = moov.Child("mvhd")!.Data!;
            return mvhd[0] == 1 ? U32(mvhd, 20) : U32(mvhd, 12);
        }

        private static Dictionary<uint, Track> ReadTracks(Box moov)
        {
            var tracks = new Dictionary<uint, Track>();
            foreach (var trak in moov.Children!.Where(c => c.Type == "trak"))
            {
                byte[] tkhd = trak.Child("tkhd")!.Data!;
                var mdia = trak.Child("mdia")!;
                byte[] mdhd = mdia.Child("mdhd")!.Data!;
                string handler = Encoding.ASCII.GetString(mdia.Child("hdlr")!.Data!, 8, 4);
                var track = new Track
                {
                    Id = tkhd[0] == 1 ? U32(tkhd, 20) : U32(tkhd, 12),
                    Timescale = mdhd[0] == 1 ? U32(mdhd, 20) : U32(mdhd, 12),
                    IsVideo = handler == "vide",
                    IsAudio = handler == "soun",
                };
                tracks[track.Id] = track;
            }

            foreach (var trex in moov.Child("mvex")?.Children?.Where(c => c.Type == "trex") ?? [])
            {
                byte[] d = trex.Data!;
                if (tracks.TryGetValue(U32(d, 4), out var track))
                {
                    track.TrexDuration = U32(d, 12);
                    track.TrexSize = U32(d, 16);
                    track.TrexFlags = U32(d, 20);
                }
            }
            return tracks;
        }

        private static void ParseMoof(Box moof, long moofStart, Dictionary<uint, Track> tracks)
        {
            foreach (var traf in moof.Children!.Where(c => c.Type == "traf"))
            {
                byte[] tfhd = traf.Child("tfhd")!.Data!;
                uint tfhdFlags = U32(tfhd, 0) & 0xFFFFFF;
                if (!tracks.TryGetValue(U32(tfhd, 4), out var track)) continue;

                int o = 8;
                long baseOffset = moofStart;
                if ((tfhdFlags & 0x1) != 0) { baseOffset = (long)U64(tfhd, o); o += 8; }
                if ((tfhdFlags & 0x2) != 0) o += 4;
                uint defaultDuration = track.TrexDuration, defaultSize = track.TrexSize, defaultFlags = track.TrexFlags;
                if ((tfhdFlags & 0x8) != 0) { defaultDuration = U32(tfhd, o); o += 4; }
                if ((tfhdFlags & 0x10) != 0) { defaultSize = U32(tfhd, o); o += 4; }
                if ((tfhdFlags & 0x20) != 0) { defaultFlags = U32(tfhd, o); }

                byte[]? tfdt = traf.Child("tfdt")?.Data;
                ulong decodeTime = tfdt == null ? track.TotalDuration : tfdt[0] == 1 ? U64(tfdt, 4) : U32(tfdt, 4);

                int fragmentFirst = track.Sizes.Count;
                bool missingDurations = false;
                long nextOffset = baseOffset;

                foreach (var trun in traf.Children!.Where(c => c.Type == "trun"))
                {
                    byte[] d = trun.Data!;
                    int version = d[0];
                    uint flags = U32(d, 0) & 0xFFFFFF;
                    uint count = U32(d, 4);
                    o = 8;
                    long offset = nextOffset;
                    if ((flags & 0x1) != 0) { offset = baseOffset + (int)U32(d, o); o += 4; }
                    uint? firstFlags = null;
                    if ((flags & 0x4) != 0) { firstFlags = U32(d, o); o += 4; }

                    long runBytes = 0;
                    for (int i = 0; i < count; i++)
                    {
                        uint duration = defaultDuration, size = defaultSize, sampleFlags = defaultFlags;
                        int cts = 0;
                        if ((flags & 0x100) != 0) { duration = U32(d, o); o += 4; }
                        if ((flags & 0x200) != 0) { size = U32(d, o); o += 4; }
                        if ((flags & 0x400) != 0) { sampleFlags = U32(d, o); o += 4; }
                        else if (i == 0 && firstFlags.HasValue) sampleFlags = firstFlags.Value;
                        if ((flags & 0x800) != 0) { cts = version == 1 ? (int)U32(d, o) : (int)U32(d, o); o += 4; }

                        if (duration == 0) missingDurations = true;
                        track.Sizes.Add(size);
                        track.Durations.Add(duration);
                        track.Cts.Add(cts);
                        if ((sampleFlags & 0x10000) == 0) track.SyncSamples.Add((uint)track.Sizes.Count);
                        runBytes += size;
                    }
                    if (count > 0) track.Chunks.Add((offset, count));
                    nextOffset = offset + runBytes;
                }

                track.Fragments.Add((fragmentFirst, track.Sizes.Count - fragmentFirst, decodeTime, missingDurations));
                track.TotalDuration = decodeTime + (ulong)track.Durations.Skip(fragmentFirst).Sum(x => (long)x);
            }
        }

        // Spreads a fragment's span (from the next fragment's decode time) over its samples
        private static void FillMissingDurations(Track track)
        {
            for (int f = 0; f < track.Fragments.Count; f++)
            {
                var (first, count, tfdt, missing) = track.Fragments[f];
                if (!missing || count == 0) continue;
                uint each;
                if (f + 1 < track.Fragments.Count)
                    each = (uint)((track.Fragments[f + 1].Tfdt - tfdt) / (ulong)count);
                else
                    each = first > 0 ? track.Durations[first - 1] : 1;
                for (int i = first; i < first + count; i++)
                    if (track.Durations[i] == 0) track.Durations[i] = Math.Max(1u, each);
            }
            if (track.Fragments.Count > 0)
            {
                var (first, _, tfdt, _) = track.Fragments[^1];
                track.TotalDuration = tfdt + (ulong)track.Durations.Skip(first).Sum(x => (long)x);
            }
        }

        private static byte[] BuildFtyp()
        {
            var payload = new List<byte>();
            payload.AddRange(Encoding.ASCII.GetBytes("isom"));
            payload.AddRange(new byte[] { 0, 0, 2, 0 });
            payload.AddRange(Encoding.ASCII.GetBytes("isomiso2mp41"));
            return Serialize(new Box { Type = "ftyp", Data = payload.ToArray() });
        }

        private static Box BuildMoov(Box original, Dictionary<uint, Track> tracks, uint movieTimescale, long virtualDataStart, long dataStart)
        {
            ulong movieDuration = 0;
            var children = new List<Box>();
            foreach (var box in original.Children!)
            {
                if (box.Type == "mvex") continue;
                if (box.Type != "trak")
                {
                    children.Add(box);
                    continue;
                }

                byte[] tkhd = box.Child("tkhd")!.Data!;
                var track = tracks[tkhd[0] == 1 ? U32(tkhd, 20) : U32(tkhd, 12)];
                ulong trackMovieDuration = track.TotalDuration * movieTimescale / Math.Max(1u, track.Timescale);
                if (track.IsVideo || movieDuration == 0) movieDuration = Math.Max(movieDuration, trackMovieDuration);
                children.Add(BuildTrak(box, track, movieTimescale, trackMovieDuration, virtualDataStart, dataStart));
            }

            var mvhd = children.First(c => c.Type == "mvhd");
            children[children.IndexOf(mvhd)] = new Box { Type = "mvhd", Data = PatchDuration(mvhd.Data!, 12, 16, 20, 24, movieDuration) };
            return new Box { Type = "moov", Children = children };
        }

        // Writes a duration into a version 0 (32-bit) or version 1 (64-bit) header field
        private static byte[] PatchDuration(byte[] data, int v0TimescaleOffset, int v0DurationOffset, int v1TimescaleOffset, int v1DurationOffset, ulong duration)
        {
            byte[] copy = (byte[])data.Clone();
            if (copy[0] == 1)
                BinaryPrimitives.WriteUInt64BigEndian(copy.AsSpan(v1DurationOffset), duration);
            else
                BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(v0DurationOffset), (uint)Math.Min(duration, uint.MaxValue));
            return copy;
        }

        private static Box BuildTrak(Box trak, Track track, uint movieTimescale, ulong trackMovieDuration, long virtualDataStart, long dataStart)
        {
            var children = new List<Box>();
            foreach (var box in trak.Children!)
            {
                switch (box.Type)
                {
                    case "tkhd":
                        // v0: flags, ctime, mtime, track_ID, reserved, duration; v1 has 64-bit times
                        children.Add(new Box { Type = "tkhd", Data = PatchDuration(box.Data!, 0, 20, 0, 28, trackMovieDuration) });
                        break;
                    case "edts":
                        children.Add(BuildEdts(box, track, movieTimescale, trackMovieDuration));
                        break;
                    case "mdia":
                        children.Add(BuildMdia(box, track, virtualDataStart, dataStart));
                        break;
                    default:
                        children.Add(box);
                        break;
                }
            }
            return new Box { Type = "trak", Children = children };
        }

        private static Box BuildEdts(Box edts, Track track, uint movieTimescale, ulong trackMovieDuration)
        {
            byte[]? elst = edts.Child("elst")?.Data;
            long mediaTime = 0;
            if (elst != null && U32(elst, 4) > 0)
                mediaTime = elst[0] == 1 ? (long)U64(elst, 16) : (int)U32(elst, 12);

            ulong segment = trackMovieDuration;
            if (track.IsAudio && mediaTime > 0)
                segment -= Math.Min(segment, (ulong)mediaTime * movieTimescale / Math.Max(1u, track.Timescale));

            byte[] data = new byte[8 + 12];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)Math.Min(segment, uint.MaxValue));
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), (int)mediaTime);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), 1 << 16);
            return new Box { Type = "edts", Children = [new Box { Type = "elst", Data = data }] };
        }

        private static Box BuildMdia(Box mdia, Track track, long virtualDataStart, long dataStart)
        {
            var children = new List<Box>();
            foreach (var box in mdia.Children!)
            {
                if (box.Type == "mdhd")
                {
                    children.Add(new Box { Type = "mdhd", Data = PatchDuration(box.Data!, 0, 16, 0, 24, track.TotalDuration) });
                }
                else if (box.Type == "minf")
                {
                    var minfChildren = box.Children!
                        .Select(c => c.Type == "stbl" ? BuildStbl(c, track, virtualDataStart, dataStart) : c)
                        .ToList();
                    children.Add(new Box { Type = "minf", Children = minfChildren });
                }
                else
                {
                    children.Add(box);
                }
            }
            return new Box { Type = "mdia", Children = children };
        }

        private static Box BuildStbl(Box stbl, Track track, long virtualDataStart, long dataStart)
        {
            var children = new List<Box> { stbl.Child("stsd")! };
            int count = track.Sizes.Count;

            // stts: run-length sample durations
            var stts = new List<(uint Count, uint Delta)>();
            foreach (uint duration in track.Durations)
            {
                if (stts.Count > 0 && stts[^1].Delta == duration) stts[^1] = (stts[^1].Count + 1, duration);
                else stts.Add((1, duration));
            }
            children.Add(FullBox("stts", 0, w =>
            {
                w.U32((uint)stts.Count);
                foreach (var (c, d) in stts) { w.U32(c); w.U32(d); }
            }));

            // ctts: signed composition offsets (version 1), only when any are non-zero
            if (track.Cts.Any(c => c != 0))
            {
                var ctts = new List<(uint Count, int Offset)>();
                foreach (int cts in track.Cts)
                {
                    if (ctts.Count > 0 && ctts[^1].Offset == cts) ctts[^1] = (ctts[^1].Count + 1, cts);
                    else ctts.Add((1, cts));
                }
                children.Add(FullBox("ctts", 1, w =>
                {
                    w.U32((uint)ctts.Count);
                    foreach (var (c, off) in ctts) { w.U32(c); w.U32((uint)off); }
                }));
            }

            // stss: omitted when every sample is a sync sample
            if (track.IsVideo && track.SyncSamples.Count < count)
            {
                children.Add(FullBox("stss", 0, w =>
                {
                    w.U32((uint)track.SyncSamples.Count);
                    foreach (uint s in track.SyncSamples) w.U32(s);
                }));
            }

            // stsc: one chunk per trun
            var stsc = new List<(uint FirstChunk, uint Samples)>();
            for (int i = 0; i < track.Chunks.Count; i++)
            {
                uint samples = track.Chunks[i].Count;
                if (stsc.Count == 0 || stsc[^1].Samples != samples) stsc.Add(((uint)i + 1, samples));
            }
            children.Add(FullBox("stsc", 0, w =>
            {
                w.U32((uint)stsc.Count);
                foreach (var (first, samples) in stsc) { w.U32(first); w.U32(samples); w.U32(1); }
            }));

            children.Add(FullBox("stsz", 0, w =>
            {
                w.U32(0);
                w.U32((uint)count);
                foreach (uint size in track.Sizes) w.U32(size);
            }));

            children.Add(FullBox("co64", 0, w =>
            {
                w.U32((uint)track.Chunks.Count);
                foreach (var (offset, _) in track.Chunks) w.U64((ulong)(virtualDataStart + (offset - dataStart)));
            }));

            return new Box { Type = "stbl", Children = children };
        }

        private sealed class BeWriter
        {
            public readonly MemoryStream Stream = new();
            private readonly byte[] _buf = new byte[8];
            public void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(_buf, v); Stream.Write(_buf, 0, 4); }
            public void U64(ulong v) { BinaryPrimitives.WriteUInt64BigEndian(_buf, v); Stream.Write(_buf, 0, 8); }
        }

        private static Box FullBox(string type, byte version, Action<BeWriter> body)
        {
            var w = new BeWriter();
            w.U32((uint)version << 24);
            body(w);
            return new Box { Type = type, Data = w.Stream.ToArray() };
        }
    }
}
