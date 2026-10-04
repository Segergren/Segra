using System.Security.Cryptography;
using System.Text;

namespace Segra.Backend.Games.Fortnite
{
    internal record Elimination(TimeSpan Time, string Eliminated, string Eliminator, bool Knocked);

    internal class Replay
    {
        public bool IsLive { get; set; }
        public DateTime Timestamp { get; set; }
        public List<Elimination> Eliminations { get; } = [];
    }

    // Reads only the elimination events of a Fortnite replay, the network data is skipped
    internal static class ReplayReader
    {
        private const uint FileMagic = 0x1CA2E27F;
        private const uint EventChunk = 3;
        private const int EliminationVersion = 10;

        private const byte Bot = 3;
        private const byte NamedBot = 16;
        private const byte Player = 17;

        public static Replay Read(string file)
        {
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            var replay = new Replay();
            byte[] key;

            try
            {
                if (reader.ReadUInt32() != FileMagic)
                    throw new InvalidDataException("Not an Unreal replay");

                reader.ReadUInt32(); // file version
                stream.Seek(reader.ReadInt32() * 20L, SeekOrigin.Current); // custom versions
                reader.ReadUInt32(); // length in ms
                reader.ReadUInt32(); // network version
                reader.ReadUInt32(); // changelist
                ReadFString(reader); // friendly name
                replay.IsLive = reader.ReadUInt32() != 0;
                replay.Timestamp = new DateTime(reader.ReadInt64(), DateTimeKind.Local);
                reader.ReadUInt32(); // compressed
                reader.ReadUInt32(); // encrypted
                key = reader.ReadBytes(reader.ReadInt32());
            }
            catch (EndOfStreamException)
            {
                // The header lands on disk a few seconds after the file is created
                replay.IsLive = true;
                return replay;
            }

            if (replay.IsLive)
                return replay;

            while (stream.Length - stream.Position >= 8)
            {
                var type = reader.ReadUInt32();
                var size = reader.ReadInt32();
                var next = stream.Position + size;
                if (size <= 0 || next > stream.Length)
                    break;

                if (type == EventChunk)
                    ReadEvent(reader, key, replay);

                stream.Position = next;
            }

            return replay;
        }

        private static void ReadEvent(BinaryReader reader, byte[] key, Replay replay)
        {
            ReadFString(reader); // id
            var group = ReadFString(reader);
            ReadFString(reader); // metadata
            var startTime = reader.ReadUInt32();
            reader.ReadUInt32(); // end time
            var data = reader.ReadBytes(reader.ReadInt32());

            if (group != "playerElim")
                return;

            using var aes = Aes.Create();
            aes.Key = key;
            using var elim = new BinaryReader(new MemoryStream(aes.DecryptEcb(data, PaddingMode.PKCS7)));

            var version = elim.ReadInt32();
            if (version != EliminationVersion)
                throw new InvalidDataException($"Unsupported playerElim version {version}");

            elim.ReadBytes(161); // unknown byte, then the victim's and eliminator's transforms
            var eliminated = ReadPlayer(elim);
            var eliminator = ReadPlayer(elim);
            elim.ReadByte(); // gun type
            var knocked = elim.ReadUInt32() != 0;
            elim.ReadUInt32(); // added in v10 (Fortnite 41.10), always 0 so far

            if (elim.BaseStream.Position != elim.BaseStream.Length)
                throw new InvalidDataException("Unknown playerElim layout");

            replay.Eliminations.Add(new Elimination(TimeSpan.FromMilliseconds(startTime), eliminated, eliminator, knocked));
        }

        private static string ReadPlayer(BinaryReader reader)
        {
            return reader.ReadByte() switch
            {
                Bot => "",
                NamedBot => ReadFString(reader),
                Player => Convert.ToHexStringLower(reader.ReadBytes(reader.ReadByte())),
                _ => ""
            };
        }

        private static string ReadFString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length == 0)
                return "";
            if (length < 0)
                return Encoding.Unicode.GetString(reader.ReadBytes(-length * 2)).TrimEnd('\0');
            return Encoding.ASCII.GetString(reader.ReadBytes(length)).TrimEnd('\0');
        }
    }
}
