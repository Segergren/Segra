using System.IO;

namespace Segra.Hotkeys
{
    /// <summary>
    /// A key combination to watch. <c>Id</c> is opaque to the broker: Segra decides what each id
    /// means (its own <c>KeybindAction</c> value), so the broker has no idea what a "bookmark" is.
    /// </summary>
    public readonly record struct KeyBinding(int Id, int[] Keys);

    /// <summary>Message discriminators sent over the broker pipe (little-endian binary).</summary>
    public enum BrokerMessage : byte
    {
        /// <summary>App -&gt; broker: replace the bindings to watch.</summary>
        SetBindings = 1,

        /// <summary>Broker -&gt; app: bindings applied.</summary>
        BindingsAck = 2,

        /// <summary>Broker -&gt; app: a binding fired (payload is its id).</summary>
        Fired = 3,

        /// <summary>App -&gt; broker: exit now.</summary>
        Shutdown = 4,
    }

    /// <summary>
    /// The wire contract shared by Segra and the uiAccess broker. This file is compiled into both
    /// (the broker includes it; Segra links it) so the framing cannot drift. Kept to plain
    /// <see cref="BinaryReader"/>/<see cref="BinaryWriter"/> calls with no action semantics.
    /// </summary>
    public static class HotkeyProtocol
    {
        public const byte Version = 2;

        public const string BrokerExeName = "Segra.Hotkeys.exe";

        /// <summary>Upper bound on bindings per message so a bad client cannot force a huge allocation.</summary>
        public const int MaxBindings = 64;

        /// <summary>Per-session pipe so each signed-in user talks to their own broker.</summary>
        public static string PipeNameForSession(int sessionId) => $"Segra_Hotkeys_{sessionId}";

        public static void WriteBindings(BinaryWriter writer, IReadOnlyList<KeyBinding> bindings)
        {
            writer.Write((byte)BrokerMessage.SetBindings);
            writer.Write(bindings.Count);
            foreach (var binding in bindings)
            {
                int keyCount = Math.Min(binding.Keys.Length, byte.MaxValue);
                writer.Write(binding.Id);
                writer.Write((byte)keyCount);
                for (int i = 0; i < keyCount; i++)
                    writer.Write(binding.Keys[i]);
            }
            writer.Flush();
        }

        public static List<KeyBinding> ReadBindings(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxBindings)
                throw new InvalidDataException($"Binding count {count} is out of range.");
            var result = new List<KeyBinding>(count);
            for (int i = 0; i < count; i++)
            {
                int id = reader.ReadInt32();
                int keyCount = reader.ReadByte();
                var keys = new int[keyCount];
                for (int j = 0; j < keyCount; j++)
                    keys[j] = reader.ReadInt32();
                result.Add(new KeyBinding(id, keys));
            }
            return result;
        }

        public static void WriteMessage(BinaryWriter writer, BrokerMessage message)
        {
            writer.Write((byte)message);
            writer.Flush();
        }

        public static BrokerMessage ReadMessage(BinaryReader reader) => (BrokerMessage)reader.ReadByte();

        public static void WriteFired(BinaryWriter writer, int id)
        {
            writer.Write((byte)BrokerMessage.Fired);
            writer.Write(id);
            writer.Flush();
        }
    }
}
