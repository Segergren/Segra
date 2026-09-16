using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Segra.Backend.Games.RainbowSixSiege
{
    internal enum ReplayEventType
    {
        Kill,
        Death,
        DefuserPlantComplete
    }

    internal class ReplayPlayer
    {
        public string Username { get; set; } = "";
        public ulong Id { get; set; }
        public string? ProfileId { get; set; }
    }

    internal record ReplayEvent(ReplayEventType Type, string? Username, string? Target, bool Headshot, double TimeInSeconds);

    internal class ReplayRound
    {
        public DateTime Timestamp { get; set; }
        public int CodeVersion { get; set; }
        public string? GameVersion { get; set; }
        public int RoundNumber { get; set; }
        public ulong RecordingPlayerId { get; set; }
        public string? RecordingProfileId { get; set; }
        public List<ReplayPlayer> Players { get; } = [];
        public List<ReplayEvent> Events { get; } = [];
    }

    // Minimal port of r6-dissect for the current game version: kills, deaths, plant completion and the round clock
    internal class ReplayReader
    {
        public const int MinCodeVersion = 9883691;

        private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];
        private static readonly byte[] TimePattern = [0x1F, 0x07, 0xEF, 0xC9];
        private static readonly byte[] PlayerPattern = [0x22, 0x07, 0x94, 0x9B, 0xDC];
        private static readonly byte[] FeedbackPattern = [0x59, 0x34, 0xE5, 0x8B, 0x04];
        private static readonly byte[] KillIndicator = [0x22, 0xD9, 0x13, 0x3C, 0xBA];
        private static readonly byte[] PlayerOperatorPattern = [0x40, 0xF2, 0x15, 0x04];
        private static readonly byte[] PlayerIdPattern = [0x33, 0xD8, 0x3D, 0x4F, 0x23];
        private static readonly byte[] PlayerSpawnPattern = [0xAF, 0x98, 0x99, 0xCA];
        private static readonly byte[] PlayerUiIdPattern = [0x38, 0xDF, 0xEE, 0x88];
        private static readonly byte[] PlayerProfileIdPattern = [0x8A, 0x50, 0x9B, 0xD0];

        private readonly ReplayRound round = new();
        private byte[] data;
        private int offset;
        private double time;
        private double lastNonZeroTime;
        private bool planted;

        private ReplayReader(byte[] raw)
        {
            data = raw;
        }

        public static ReplayRound Read(string file)
        {
            var reader = new ReplayReader(File.ReadAllBytes(file));
            reader.ReadHeader();
            reader.Decompress();
            reader.ReadEvents();
            return reader.round;
        }

        private void ReadHeader()
        {
            if (!Bytes(7).SequenceEqual("dissect"u8))
                throw new InvalidDataException("Not a dissect replay");

            var zeros = 0;
            var runs = 0;
            while (runs != 2)
            {
                if (Byte() == 0)
                {
                    if (zeros != 6)
                        zeros++;
                    else
                    {
                        zeros = 0;
                        runs++;
                    }
                }
                else if (zeros > 0)
                    zeros = 0;
            }

            var props = new Dictionary<string, string>();
            ReplayPlayer? player = null;
            while (true)
            {
                var key = HeaderString();
                var value = HeaderString();
                switch (key)
                {
                    case "playerid":
                        player = new ReplayPlayer { Id = ulong.Parse(value, CultureInfo.InvariantCulture) };
                        round.Players.Add(player);
                        break;
                    case "playername" when player != null:
                        player.Username = value;
                        break;
                    case "playlistcategory" or "id":
                        player = null;
                        break;
                }

                props[key] = value;
                if (key == "teamscore1")
                    break;
            }

            round.CodeVersion = int.Parse(props["code"], CultureInfo.InvariantCulture);
            if (round.CodeVersion < MinCodeVersion)
                throw new NotSupportedException($"Replay code version {round.CodeVersion} is older than {MinCodeVersion}");

            round.GameVersion = props.GetValueOrDefault("version");
            round.Timestamp = DateTime.ParseExact(props["datetime"], "yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
            round.RoundNumber = int.Parse(props["roundnumber"], CultureInfo.InvariantCulture);
            round.RecordingPlayerId = ulong.Parse(props["recordingplayerid"], CultureInfo.InvariantCulture);
            round.RecordingProfileId = props.GetValueOrDefault("recordingprofileid");
        }

        private string HeaderString()
        {
            int size = Byte();
            if (Bytes(7).IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidDataException("Invalid header string separator");
            return Encoding.UTF8.GetString(Bytes(size));
        }

        private unsafe void Decompress()
        {
            using var output = new MemoryStream();
            while (true)
            {
                var index = data.AsSpan(offset).IndexOf(ZstdMagic);
                if (index < 0)
                    break;

                var start = offset + index;
                nuint size;
                fixed (byte* p = &data[start])
                    size = Methods.ZSTD_findFrameCompressedSize(p, (nuint)(data.Length - start));

                if (Methods.ZSTD_isError(size))
                {
                    offset = start + ZstdMagic.Length;
                    continue;
                }

                using var stream = new DecompressionStream(new MemoryStream(data, start, (int)size));
                stream.CopyTo(output);
                offset = start + (int)size;
            }

            data = output.ToArray();
            offset = 0;
        }

        private void ReadEvents()
        {
            var matches = new List<(int Offset, byte[] Pattern)>();
            foreach (var pattern in new[] { TimePattern, PlayerPattern, FeedbackPattern })
            {
                var position = 0;
                while (true)
                {
                    var index = data.AsSpan(position).IndexOf(pattern);
                    if (index < 0)
                        break;
                    position += index + pattern.Length;
                    matches.Add((position, pattern));
                }
            }

            foreach (var (matchOffset, pattern) in matches.OrderBy(m => m.Offset))
            {
                offset = matchOffset;
                try
                {
                    if (pattern == TimePattern)
                        ReadTime();
                    else if (pattern == PlayerPattern)
                        ReadPlayer();
                    else
                        ReadFeedback();
                }
                catch (EndOfStreamException)
                {
                }
            }
        }

        private void ReadPlayer()
        {
            var username = String();
            Seek(PlayerOperatorPattern);
            Skip(8);
            if (Byte() == 0x9D)
                return;
            if (Uint64() == 0)
                return;
            if (Byte() != 0x22)
                return;

            Seek(PlayerIdPattern);
            Bytes(4);
            Seek(PlayerSpawnPattern);
            if (String().Length == 0)
            {
                Skip(10);
                if (Byte() != 0x1B)
                    return;
            }

            Seek(PlayerUiIdPattern);
            Skip(13);
            Uint64();

            if (string.IsNullOrEmpty(round.RecordingProfileId) || username.Length == 0)
                return;

            Seek(PlayerProfileIdPattern);
            var profileId = String();

            var player = round.Players.FirstOrDefault(p => p.Username == username);
            if (player == null)
                round.Players.Add(new ReplayPlayer { Username = username, ProfileId = profileId });
            else
                player.ProfileId = profileId;
        }

        private void ReadFeedback()
        {
            Skip(38);
            if (Byte() != 0)
                return;
            if (!Bytes(5).SequenceEqual(KillIndicator))
                return;

            var username = String();
            Skip(15);
            var target = String();
            // The clock resets to 0 before the round-ending kill packet is written
            var clock = time > 0 ? time : lastNonZeroTime;
            if (username.Length == 0)
            {
                if (target.Length > 0)
                    round.Events.Add(new ReplayEvent(ReplayEventType.Death, target, null, false, clock));
                return;
            }

            Skip(56);
            var headshot = Byte() == 1;
            if (round.Events.Any(e => e.Type == ReplayEventType.Kill && e.Username == username && e.Target == target))
                return;

            round.Events.Add(new ReplayEvent(ReplayEventType.Kill, username, target, headshot, clock));
        }

        private void ReadTime()
        {
            var value = Uint32();
            // A plant switches the clock from the action countdown straight to the 45 s defuse countdown
            if (!planted && value is 44 or 45 && time > 0 && time != value + 1)
            {
                planted = true;
                round.Events.Add(new ReplayEvent(ReplayEventType.DefuserPlantComplete, null, null, false, time));
            }

            time = value;
            if (value > 0)
                lastNonZeroTime = value;
        }

        private void Seek(byte[] pattern)
        {
            var index = data.AsSpan(offset).IndexOf(pattern);
            if (index < 0)
                throw new EndOfStreamException();
            offset += index + pattern.Length;
        }

        private void Skip(int count)
        {
            if (offset + count >= data.Length)
                throw new EndOfStreamException();
            offset += count;
        }

        private ReadOnlySpan<byte> Bytes(int count)
        {
            Skip(count);
            return data.AsSpan(offset - count, count);
        }

        private byte Byte() => Bytes(1)[0];

        private string String() => Encoding.UTF8.GetString(Bytes(Byte()));

        private uint Uint32()
        {
            Skip(1);
            return BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        }

        private ulong Uint64()
        {
            Skip(1);
            return BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));
        }
    }
}
