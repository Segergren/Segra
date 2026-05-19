using Serilog;
using System.Text;

namespace Segra.Backend.Games.WarThunder
{
    internal static class ClogDecoder
    {
        private static readonly byte[] XorKey =
        {
            130, 135, 151, 64, 141, 139, 70, 11, 187, 115,
            148, 3, 229, 179, 131, 83, 105, 107, 131, 218,
            149, 175, 74, 35, 135, 229, 151, 172, 36, 88,
            175, 54, 78, 225, 90, 249, 241, 1, 75, 177,
            173, 182, 76, 76, 250, 116, 40, 105, 194, 139,
            17, 23, 213, 182, 71, 206, 179, 183, 205, 85,
            254, 249, 193, 36, 255, 174, 144, 46, 73, 108,
            78, 9, 146, 129, 78, 103, 188, 107, 156, 222,
            177, 15, 104, 186, 139, 128, 68, 5, 135, 94,
            243, 78, 254, 9, 151, 50, 192, 173, 159, 233,
            187, 253, 77, 6, 145, 80, 137, 110, 224, 232,
            238, 153, 83, 0, 60, 166, 184, 34, 65, 50,
            177, 189, 245, 40, 80, 224, 114, 174
        };

        public static string Decode(string filePath)
        {
            byte[] bytes;
            try
            {
                using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                Log.Warning($"[WT] Failed to read clog {filePath}: {ex.Message}");
                return string.Empty;
            }

            var output = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                output[i] = (byte)(bytes[i] ^ XorKey[i % XorKey.Length]);
            }
            return Encoding.UTF8.GetString(output);
        }

        public static string ExtractNickname(string decodedText)
        {
            foreach (var line in decodedText.Split('\n'))
            {
                if (line.Contains("successfully passed yuplay authorization, use jwt token"))
                {
                    var afterBracket = line.Split('[').Skip(1).FirstOrDefault();
                    var nick = afterBracket?.Split(' ').LastOrDefault();
                    if (!string.IsNullOrEmpty(nick)) return nick.Trim();
                }
                if (line.Contains("\"nick\""))
                {
                    foreach (var part in line.Split(','))
                    {
                        if (!part.Contains("\"nick\"")) continue;
                        var value = part.Split(':').Skip(1).FirstOrDefault();
                        var nick = value?.Trim().Trim('"');
                        if (!string.IsNullOrEmpty(nick)) return nick;
                    }
                }
            }
            return string.Empty;
        }

        public static string? ResolveClogDirectory(string? exePath)
        {
            if (!string.IsNullOrEmpty(exePath))
            {
                var dir = Path.GetDirectoryName(exePath);
                while (!string.IsNullOrEmpty(dir))
                {
                    var candidate = Path.Combine(dir, ".game_logs");
                    if (Directory.Exists(candidate)) return candidate;
                    var name = Path.GetFileName(dir);
                    if (string.Equals(name, "War Thunder", StringComparison.OrdinalIgnoreCase)) break;
                    dir = Path.GetDirectoryName(dir);
                }
            }

            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WarThunder", ".game_logs");
            if (Directory.Exists(fallback)) return fallback;

            return null;
        }

        public static string? GetMostRecentClog(string folder)
        {
            try
            {
                return new DirectoryInfo(folder)
                    .EnumerateFiles("*.clog")
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .FirstOrDefault()?.FullName;
            }
            catch (Exception ex)
            {
                Log.Warning($"[WT] Failed to enumerate clog dir {folder}: {ex.Message}");
                return null;
            }
        }
    }
}
