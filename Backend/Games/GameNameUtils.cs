using System.Text.RegularExpressions;

namespace Segra.Backend.Games
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

        public static string SmartFormatGameName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "Unknown";

            string result = input.Trim();

            // Handle Desktop special case
            if (result.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
                return "Desktop Recording";

            // First, identify and preserve potential acronyms (2-6 consecutive uppercase letters)
            var acronymMatches = Regex.Matches(result, @"\b[A-Z]{2,6}\b");
            var acronymPlaceholders = new Dictionary<string, string>();
            int placeholderIndex = 0;

            foreach (Match match in acronymMatches)
            {
                string placeholder = $"__ACRONYM_{placeholderIndex}__";
                acronymPlaceholders[placeholder] = match.Value;
                result = result.Replace(match.Value, placeholder);
                placeholderIndex++;
            }

            // Add spaces before capital letters (CamelCase handling)
            result = Regex.Replace(result, @"(?<!^)(?=[A-Z][a-z])|(?<=[a-z])(?=[A-Z])", " ");

            // Add spaces before numbers that follow letters
            result = Regex.Replace(result, @"(?<=[a-zA-Z])(?=\d)", " ");

            // Add spaces after numbers that are followed by letters
            result = Regex.Replace(result, @"(?<=\d)(?=[a-zA-Z])", " ");

            // Clean up multiple spaces
            result = Regex.Replace(result, @"\s+", " ").Trim();

            // Restore acronyms
            foreach (var placeholder in acronymPlaceholders)
            {
                result = result.Replace(placeholder.Key, placeholder.Value);
            }

            // Split into words for processing
            var words = result.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var processedWords = new List<string>();

            foreach (string word in words)
            {
                string processedWord = word;

                // Keep acronyms as they are (2-6 uppercase letters)
                if (word.Length >= 2 && word.Length <= 6 && word.All(char.IsUpper) && word.All(char.IsLetter))
                {
                    processedWord = word;
                }
                // Handle connecting words as lowercase (except if first word)
                else if (processedWords.Count > 0 &&
                         (word.Equals("of", StringComparison.OrdinalIgnoreCase) ||
                          word.Equals("the", StringComparison.OrdinalIgnoreCase) ||
                          word.Equals("and", StringComparison.OrdinalIgnoreCase) ||
                          word.Equals("in", StringComparison.OrdinalIgnoreCase) ||
                          word.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                          word.Equals("at", StringComparison.OrdinalIgnoreCase) ||
                          word.Equals("to", StringComparison.OrdinalIgnoreCase) ||
                          word.Equals("for", StringComparison.OrdinalIgnoreCase)))
                {
                    processedWord = word.ToLower();
                }
                // Detect Roman numerals (proper patterns only)
                else if (Regex.IsMatch(word, @"^(I|II|III|IV|V|VI|VII|VIII|IX|X|XI|XII|XIII|XIV|XV|XVI|XVII|XVIII|XIX|XX)$", RegexOptions.IgnoreCase))
                {
                    processedWord = word.ToUpper();
                }
                // Regular title case for other words
                else
                {
                    processedWord = char.ToUpper(word[0]) + (word.Length > 1 ? word.Substring(1).ToLower() : "");
                }

                processedWords.Add(processedWord);
            }

            return string.Join(" ", processedWords);
        }
    }
}
