using System.Text;
using Serilog;

namespace Segra.Backend.Media
{
    /// <summary>
    /// Minimal ISO-BMFF (MP4) box reader used to extract per-track audio names
    /// from Segra/OBS recordings. OBS writes each audio encoder's name into a
    /// custom `trak/udta/name` atom that FFmpeg's mov demuxer does not surface,
    /// so we walk the box tree ourselves.
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
}
