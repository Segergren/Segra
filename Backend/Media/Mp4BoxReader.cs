using Serilog;
using System.Text;

namespace Segra.Backend.Media
{
    /// <summary>
    /// Minimal ISO-BMFF (MP4) box reader used to extract per-track audio names
    /// from Segra/OBS recordings. OBS writes each audio encoder's name into a
    /// custom `trak/udta/name` atom that FFmpeg's mov demuxer does not surface,
    /// so we walk the box tree ourselves. Also reads the video decoder configuration.
    /// </summary>
    internal static class Mp4BoxReader
    {
        /// <summary>
        /// Returns the raw per-track names of all audio tracks in the file, in
        /// container order. An entry is null when the track has no `udta/name`
        /// atom, which lets callers fall back to other probes (e.g. ffmpeg's
        /// standard title/handler_name tags) before labeling. Returns null
        /// when the file isn't parsable as ISO-BMFF, or when it contains fewer
        /// than two audio tracks.
        /// </summary>
        public static async Task<List<string?>?> ReadAudioTrackNamesAsync(string filePath)
        {
            try
            {
                await using var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 65536,
                    useAsync: true);

                byte[]? moov = await ReadTopLevelBoxPayloadAsync(fs, "moov");
                if (moov == null) return null;

                var rawNames = new List<string?>();
                ParseMoov(moov, rawNames);

                return rawNames.Count >= 2 ? rawNames : null;
            }
            catch (Exception ex)
            {
                Log.Warning($"Mp4BoxReader failed for {filePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns the decoder configuration of the video track and of the audio tracks, or
        /// null when the file isn't parsable or uses a layout stream copy can't rely on.
        /// </summary>
        public static async Task<StreamFormats?> ReadStreamFormatsAsync(string filePath)
        {
            try
            {
                await using var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 65536,
                    useAsync: true);

                byte[]? moov = await ReadTopLevelBoxPayloadAsync(fs, "moov");
                return moov == null ? null : FindStreamFormats(moov);
            }
            catch (Exception ex)
            {
                Log.Warning($"Mp4BoxReader failed for {filePath}: {ex.Message}");
                return null;
            }
        }

        // Scans top-level boxes and returns the payload bytes of the first box
        // matching `fourCc`, or null if not found. Handles both 32-bit and
        // 64-bit box sizes and the "extends to end-of-file" sentinel.
        private static async Task<byte[]?> ReadTopLevelBoxPayloadAsync(FileStream fs, string fourCc)
        {
            long fileSize = fs.Length;
            long pos = 0;
            byte[] header = new byte[16];

            while (pos + 8 <= fileSize)
            {
                fs.Position = pos;
                await fs.ReadExactlyAsync(header.AsMemory(0, 8));

                uint boxSize32 = ReadUInt32BE(header, 0);
                string type = Encoding.ASCII.GetString(header, 4, 4);

                long payloadStart;
                long totalBoxSize;
                if (boxSize32 == 1)
                {
                    await fs.ReadExactlyAsync(header.AsMemory(8, 8));
                    totalBoxSize = (long)ReadUInt64BE(header, 8);
                    payloadStart = pos + 16;
                }
                else if (boxSize32 == 0)
                {
                    totalBoxSize = fileSize - pos;
                    payloadStart = pos + 8;
                }
                else
                {
                    totalBoxSize = boxSize32;
                    payloadStart = pos + 8;
                }

                if (totalBoxSize < 8 || pos + totalBoxSize > fileSize) return null;

                if (type == fourCc)
                {
                    long payloadSize = (pos + totalBoxSize) - payloadStart;
                    if (payloadSize < 0 || payloadSize > int.MaxValue) return null;
                    byte[] payload = new byte[payloadSize];
                    fs.Position = payloadStart;
                    await fs.ReadExactlyAsync(payload);
                    return payload;
                }

                pos += totalBoxSize;
            }

            return null;
        }

        // Walks `trak` boxes inside the moov payload and, for each audio trak,
        // appends its `udta/name` content (or null if absent) in container order.
        private static void ParseMoov(byte[] moov, List<string?> audioTrackNames)
        {
            foreach (var trak in EnumerateBoxes(moov, 0, moov.Length))
            {
                if (trak.Type != "trak") continue;

                bool isAudio = false;
                string? trackName = null;

                foreach (var child in EnumerateBoxes(moov, trak.PayloadStart, trak.PayloadLength))
                {
                    if (child.Type == "mdia")
                    {
                        foreach (var mdiaChild in EnumerateBoxes(moov, child.PayloadStart, child.PayloadLength))
                        {
                            if (mdiaChild.Type != "hdlr") continue;
                            // hdlr: fullbox(4) + pre_defined(4) + handler_type(4) + ...
                            if (mdiaChild.PayloadLength >= 12)
                            {
                                string handlerType = Encoding.ASCII.GetString(moov, mdiaChild.PayloadStart + 8, 4);
                                isAudio = handlerType == "soun";
                            }
                            break;
                        }
                    }
                    else if (child.Type == "udta")
                    {
                        foreach (var udtaChild in EnumerateBoxes(moov, child.PayloadStart, child.PayloadLength))
                        {
                            if (udtaChild.Type != "name") continue;
                            int len = udtaChild.PayloadLength;
                            while (len > 0 && moov[udtaChild.PayloadStart + len - 1] == 0) len--;
                            if (len > 0)
                            {
                                trackName = Encoding.UTF8.GetString(moov, udtaChild.PayloadStart, len);
                            }
                            break;
                        }
                    }
                }

                if (isAudio)
                {
                    audioTrackNames.Add(trackName);
                }
            }
        }

        private static StreamFormats? FindStreamFormats(byte[] moov)
        {
            byte[]? video = null;
            var audio = new List<byte>();
            bool seenOtherTrack = false;

            foreach (var trak in EnumerateBoxes(moov, 0, moov.Length))
            {
                if (trak.Type != "trak" || FindChild(moov, trak, "mdia") is not { } mdia) continue;

                // hdlr: fullbox(4) + pre_defined(4) + handler_type(4) + ...
                if (FindChild(moov, mdia, "hdlr") is not { PayloadLength: >= 12 } hdlr) return null;
                string handlerType = Encoding.ASCII.GetString(moov, hdlr.PayloadStart + 8, 4);
                if (handlerType != "vide" && handlerType != "soun")
                {
                    seenOtherTrack = true;
                    continue;
                }

                // Stream copy pairs streams by index, so audio and video must come before other tracks
                if (seenOtherTrack) return null;

                // stsd: fullbox(4) + entry_count(4), then the sample entries
                if (FindChild(moov, mdia, "minf") is not { } minf ||
                    FindChild(moov, minf, "stbl") is not { } stbl ||
                    FindChild(moov, stbl, "stsd") is not { PayloadLength: > 8 } stsd)
                    return null;
                BoxSpan entry = EnumerateBoxes(moov, stsd.PayloadStart + 8, stsd.PayloadLength - 8).FirstOrDefault();

                if (handlerType == "vide")
                {
                    if (video != null) return null;
                    video = ReadVideoConfig(moov, entry);
                    if (video == null) return null;
                }
                else
                {
                    byte[]? config = ReadAudioConfig(moov, entry);
                    if (config == null) return null;
                    audio.AddRange(config);
                }
            }
            return video == null ? null : new StreamFormats(video, audio.ToArray());
        }

        // The sample entry type plus its avcC, hvcC, av1C or vpcC box, which holds the encoder headers
        private static byte[]? ReadVideoConfig(byte[] moov, BoxSpan entry)
        {
            // A visual sample entry has 78 bytes of fixed fields before its child boxes
            if (entry.PayloadLength <= 78) return null;

            foreach (var child in EnumerateBoxes(moov, entry.PayloadStart + 78, entry.PayloadLength - 78))
            {
                if (child.Type is "avcC" or "hvcC" or "av1C" or "vpcC")
                    return [.. Encoding.ASCII.GetBytes(entry.Type), .. moov[child.PayloadStart..(child.PayloadStart + child.PayloadLength)]];
            }
            return null;
        }

        // Channel count, sample rate and the AAC AudioSpecificConfig. The esds bitrate fields
        // differ from file to file, so the box as a whole can't be compared.
        private static byte[]? ReadAudioConfig(byte[] moov, BoxSpan entry)
        {
            // An audio sample entry (version 0) has 28 bytes of fixed fields before its child boxes
            if (entry.Type != "mp4a" || entry.PayloadLength <= 28 || ReadUInt16BE(moov, entry.PayloadStart + 8) != 0)
                return null;
            if (FindChild(moov, new BoxSpan(entry.Type, entry.PayloadStart + 28, entry.PayloadLength - 28), "esds") is not { } esds)
                return null;

            // esds: fullbox(4), then ES_Descriptor (0x03) > DecoderConfigDescriptor (0x04) > DecoderSpecificInfo (0x05)
            int pos = esds.PayloadStart + 4;
            int end = esds.PayloadStart + esds.PayloadLength;
            if (!ReadDescriptorHeader(moov, ref pos, end, 0x03, out _) || pos + 3 > end) return null;
            byte flags = moov[pos + 2];
            pos += 3;
            if ((flags & 0x80) != 0) pos += 2;
            if ((flags & 0x40) != 0 && pos < end) pos += 1 + moov[pos];
            if ((flags & 0x20) != 0) pos += 2;
            // objectTypeIndication(1), streamType(1), bufferSizeDB(3), maxBitrate(4), avgBitrate(4)
            if (!ReadDescriptorHeader(moov, ref pos, end, 0x04, out _) || pos + 13 > end) return null;
            byte objectType = moov[pos];
            pos += 13;
            if (!ReadDescriptorHeader(moov, ref pos, end, 0x05, out int length) || length > 255 || pos + length > end)
                return null;

            return
            [
                .. moov[(entry.PayloadStart + 16)..(entry.PayloadStart + 18)], // channel count
                .. moov[(entry.PayloadStart + 24)..(entry.PayloadStart + 28)], // sample rate
                objectType,
                (byte)length,
                .. moov[pos..(pos + length)],
            ];
        }

        // MPEG-4 descriptor header: a tag byte, then a size of up to four 7-bit groups
        private static bool ReadDescriptorHeader(byte[] data, ref int pos, int end, byte tag, out int length)
        {
            length = 0;
            if (pos >= end || data[pos++] != tag) return false;
            for (int i = 0; i < 4 && pos < end; i++)
            {
                byte b = data[pos++];
                length = (length << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }

        private static BoxSpan? FindChild(byte[] data, BoxSpan parent, string type)
        {
            foreach (var box in EnumerateBoxes(data, parent.PayloadStart, parent.PayloadLength))
            {
                if (box.Type == type) return box;
            }
            return null;
        }

        // Iterates direct children of a box region, yielding each child's
        // four-cc type and payload span.
        private static IEnumerable<BoxSpan> EnumerateBoxes(byte[] data, int start, int length)
        {
            int pos = start;
            int end = start + length;
            while (pos + 8 <= end)
            {
                uint boxSize32 = ReadUInt32BE(data, pos);
                string type = Encoding.ASCII.GetString(data, pos + 4, 4);

                int payloadStart;
                int totalSize;
                if (boxSize32 == 1)
                {
                    if (pos + 16 > end) yield break;
                    ulong large = ReadUInt64BE(data, pos + 8);
                    if (large > int.MaxValue) yield break;
                    totalSize = (int)large;
                    payloadStart = pos + 16;
                }
                else if (boxSize32 == 0)
                {
                    totalSize = end - pos;
                    payloadStart = pos + 8;
                }
                else
                {
                    totalSize = (int)boxSize32;
                    payloadStart = pos + 8;
                }

                if (totalSize < 8 || pos + totalSize > end) yield break;

                int payloadLength = (pos + totalSize) - payloadStart;
                yield return new BoxSpan(type, payloadStart, payloadLength);
                pos += totalSize;
            }
        }

        private readonly record struct BoxSpan(string Type, int PayloadStart, int PayloadLength);

        private static ushort ReadUInt16BE(byte[] buf, int offset)
        {
            return (ushort)((buf[offset] << 8) | buf[offset + 1]);
        }

        private static uint ReadUInt32BE(byte[] buf, int offset)
        {
            return ((uint)buf[offset] << 24)
                 | ((uint)buf[offset + 1] << 16)
                 | ((uint)buf[offset + 2] << 8)
                 | buf[offset + 3];
        }

        private static ulong ReadUInt64BE(byte[] buf, int offset)
        {
            return ((ulong)ReadUInt32BE(buf, offset) << 32) | ReadUInt32BE(buf, offset + 4);
        }
    }

    /// <summary>
    /// Decoder configurations of a file's video track and of its audio tracks in order.
    /// Files whose bytes match can be joined by stream copy.
    /// </summary>
    internal sealed record StreamFormats(byte[] Video, byte[] Audio);
}
