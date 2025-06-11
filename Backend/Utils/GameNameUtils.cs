using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Segra.Backend.Utils
{
    internal static class GameNameUtils
    {
        public const string SteamAppsCommonPath = "/steamapps/common/";


        public static string GetGameNameOrDefault(string defaultGameName, string exeFilePath)
        {
            if (string.IsNullOrWhiteSpace(exeFilePath))
                return defaultGameName;

            try
            {
                // Normalize the path separators
                string normalizedPath = exeFilePath.Replace("\\", "/");
                int commonPathIndex = normalizedPath.IndexOf(SteamAppsCommonPath, StringComparison.OrdinalIgnoreCase);

                if (commonPathIndex == -1)
                {
                    // The expected segment "/steamapps/common/" was not found
                    return defaultGameName;
                }

                // Extract the install directory name
                int installDirStart = commonPathIndex + SteamAppsCommonPath.Length;
                int installDirEnd = normalizedPath.IndexOf('/', installDirStart);
                string installDir = installDirEnd != -1
                    ? normalizedPath.Substring(installDirStart, installDirEnd - installDirStart)
                    : normalizedPath.Substring(installDirStart);

                // Construct the Steam apps directory path
                if (commonPathIndex <= 0)
                {
                    // Invalid path index, return the install directory name
                    return installDir;
                }
                
                string pathSegment = normalizedPath.Substring(0, commonPathIndex);
                string? baseDir = Path.GetDirectoryName(pathSegment);
                if (baseDir == null)
                {
                    // If we can't get a valid directory, return the install directory name
                    return installDir;
                }
                
                string steamAppsDir = Path.Combine(
                    baseDir,
                    "Steam",
                    "steamapps"
                );

                if (!Directory.Exists(steamAppsDir))
                {
                    // If the steamapps directory doesn't exist, return the install directory name
                    return installDir;
                }

                // Iterate through all .acf files to find a matching install directory
                foreach (string acfFile in Directory.EnumerateFiles(steamAppsDir, "*.acf"))
                {
                    string content = File.ReadAllText(acfFile);
                    string acfInstallDir = ExtractAcfValue(content, "installdir");
                    string acfName = ExtractAcfValue(content, "name");

                    if (string.Equals(acfInstallDir, installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return string.IsNullOrWhiteSpace(acfName) ? installDir : acfName;
                    }
                }

                // If no matching .acf file is found, return the defaultGameName or installDir
                return string.IsNullOrEmpty(defaultGameName) ? installDir : defaultGameName;
            }
            catch
            {
                // In case of any exceptions, return the default game name
                return defaultGameName;
            }
        }

        private static string ExtractAcfValue(string content, string key)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(key))
                return string.Empty;

            // Regex pattern to match the key-value pair, e.g., "key"    "value"
            string pattern = $"\"{Regex.Escape(key)}\"\\s+\"([^\"]+)\"";
            var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);

            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }
    }
}
