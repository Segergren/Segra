using Serilog;
using System.Text;

namespace Segra.Backend.Games.MecchaChameleon
{
    /// <summary>
    /// Reads the player's chosen in-game name from MECCHA CHAMELEON's Unreal Engine save file.
    /// The game lets players set a custom display name (independent of their Steam name) and that
    /// custom name is what appears in the kill feed, so it is the correct thing to match OCR'd
    /// feed lines against. It is stored under
    ///   %LOCALAPPDATA%\Chameleon\Saved\SaveGames\cLeon_Default_&lt;EOS-account&gt;.sav
    /// as a GVAS (Unreal SaveGame) blob: a "BaseDatas" map whose "CustomPlayerName" entry holds
    /// the value in an "AsString" StrProperty. Returns null if unset or unreadable, so the caller
    /// can fall back to the Steam persona name.
    /// </summary>
    internal static class MecchaSaveReader
    {
        private const string CustomPlayerNameKey = "CustomPlayerName";

        private static string? _cachedName;
        private static string? _cachedFile;
        private static DateTime _cachedWriteUtc = DateTime.MinValue;

        public static string? GetCustomPlayerName()
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Chameleon", "Saved", "SaveGames");
                if (!Directory.Exists(dir))
                    return null;

                // The filename carries the EOS account id; if several accounts have played on this
                // machine there will be one save each, so use the most recently written one.
                var save = new DirectoryInfo(dir)
                    .GetFiles("cLeon_Default_*.sav")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (save == null)
                    return null;

                // Cache by (path, mtime): only re-parse when the save actually changes (e.g. the
                // player renames themselves), so repeated kill-feed resolves don't re-read the file.
                if (save.FullName == _cachedFile && save.LastWriteTimeUtc == _cachedWriteUtc)
                    return _cachedName;

                _cachedFile = save.FullName;
                _cachedWriteUtc = save.LastWriteTimeUtc;
                _cachedName = ExtractCustomPlayerName(File.ReadAllBytes(save.FullName));

                if (_cachedName != null)
                    Log.Information($"[MECCHA] Resolved in-game player name '{_cachedName}' from save");

                return _cachedName;
            }
            catch (Exception ex)
            {
                Log.Warning($"[MECCHA] Failed to read in-game player name from save: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Locates the "CustomPlayerName" map key in the GVAS blob and walks the tagged properties
        /// of its struct value to the "AsString" StrProperty. Returns null if the key is missing or
        /// the stored name is empty.
        /// </summary>
        private static string? ExtractCustomPlayerName(byte[] data)
        {
            int offset = IndexOfAscii(data, CustomPlayerNameKey);
            if (offset < 0)
                return null;

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            // Skip the key's text plus its FString null terminator; the value struct's tagged
            // properties (AsInt / AsFloat / AsString ...) follow, terminated by a "None" name.
            ms.Position = offset + CustomPlayerNameKey.Length + 1;

            for (int i = 0; i < 20; i++)
            {
                string name = ReadFString(br);
                if (string.IsNullOrEmpty(name) || name == "None")
                    return null;

                string type = ReadFString(br);
                long size = br.ReadInt64();
                br.ReadByte(); // property-guid presence flag (0 here)

                if (name.StartsWith("AsString", StringComparison.Ordinal))
                {
                    var value = ReadFString(br);
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }

                // Skip the value of properties before AsString.
                switch (type)
                {
                    case "IntProperty": br.ReadInt32(); break;
                    case "DoubleProperty": br.ReadDouble(); break;
                    default: br.ReadBytes((int)size); break;
                }
            }

            return null;
        }

        /// <summary>
        /// Reads an Unreal FString: int32 length prefix, then either an ASCII buffer (positive
        /// length) or a UTF-16 buffer (negative length, i.e. any name with special characters like
        /// "degröna"), both null-terminated.
        /// </summary>
        private static string ReadFString(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len == 0)
                return "";
            if (len > 0)
                return Encoding.ASCII.GetString(br.ReadBytes(len)).TrimEnd('\0');
            return Encoding.Unicode.GetString(br.ReadBytes(-len * 2)).TrimEnd('\0');
        }

        private static int IndexOfAscii(byte[] data, string ascii)
        {
            var pattern = Encoding.ASCII.GetBytes(ascii);
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        hit = false;
                        break;
                    }
                }
                if (hit)
                    return i;
            }
            return -1;
        }
    }
}
