using Segra.Backend.App;
using Serilog;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Segra.Backend.Games
{
    public static class GameUtils
    {
        private static HashSet<string> _gameExePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _exeToGameName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, int> _exeToIgdbId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _exeToCoverImageId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static List<GameEntry> _gamesList = new List<GameEntry>();
        private static BlacklistEntry _blacklist = new BlacklistEntry();
        private static readonly ConcurrentDictionary<string, Regex> _wildcardRegexCache = new();
        private static bool _isInitialized = false;

        public static async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await DownloadGamesJsonIfNeededAsync();
            LoadGamesFromJson();
            await DownloadBlacklistJsonIfNeededAsync();
            LoadBlacklistFromJson();
            _isInitialized = true;
        }

        public static bool MatchesExePattern(string exePath, string pattern)
        {
            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(pattern))
                return false;

            string normalizedPath = exePath.Replace("\\", "/");
            string fileName = Path.GetFileName(exePath);
            string normalizedPattern = pattern.Replace("\\", "/");
            return MatchesGamePattern(normalizedPath, fileName, normalizedPattern);
        }

        public static bool IsGameExePath(string exePath)
        {
            if (!_isInitialized || string.IsNullOrEmpty(exePath))
                return false;

            string normalizedPath = exePath.Replace("\\", "/");
            string fileName = Path.GetFileName(exePath);

            foreach (var gamePath in _gameExePaths)
            {
                if (MatchesGamePattern(normalizedPath, fileName, gamePath))
                    return true;
            }

            return false;
        }

        public static string? GetGameNameFromExePath(string exePath)
        {
            if (!_isInitialized || string.IsNullOrEmpty(exePath))
                return null;

            string normalizedPath = exePath.Replace("\\", "/");
            string fileName = Path.GetFileName(exePath);

            foreach (var entry in _exeToGameName)
            {
                if (MatchesGamePattern(normalizedPath, fileName, entry.Key))
                    return entry.Value;
            }

            return null;
        }

        public static int? GetIgdbIdFromExePath(string exePath)
        {
            if (!_isInitialized || string.IsNullOrEmpty(exePath))
                return null;

            string normalizedPath = exePath.Replace("\\", "/");
            string fileName = Path.GetFileName(exePath);

            foreach (var entry in _exeToIgdbId)
            {
                if (MatchesGamePattern(normalizedPath, fileName, entry.Key))
                    return entry.Value;
            }

            return null;
        }

        public static string? GetCoverImageIdFromExePath(string exePath)
        {
            if (!_isInitialized || string.IsNullOrEmpty(exePath))
                return null;

            string normalizedPath = exePath.Replace("\\", "/");
            string fileName = Path.GetFileName(exePath);

            foreach (var entry in _exeToCoverImageId)
            {
                if (MatchesGamePattern(normalizedPath, fileName, entry.Key))
                    return entry.Value;
            }

            return null;
        }

        public static bool HasKnownGameExeInFolder(string gameFolderName, string basePath)
        {
            if (!_isInitialized) return false;

            string folderPrefix = gameFolderName + "/";

            foreach (var gamePath in _gameExePaths)
            {
                if (!gamePath.Contains('/')) continue;
                if (!gamePath.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                if (gamePath.Contains('*'))
                {
                    string relativePath = gamePath.Substring(folderPrefix.Length).Replace("/", "\\");
                    string subDir = Path.GetDirectoryName(relativePath) ?? "";
                    string searchPattern = Path.GetFileName(relativePath);
                    string directory = Path.Combine(basePath, gameFolderName, subDir);

                    if (Directory.Exists(directory))
                    {
                        try
                        {
                            if (Directory.EnumerateFiles(directory, searchPattern).Any())
                            {
                                Log.Information($"Found known game executable matching wildcard pattern in folder: {directory}\\{searchPattern}");
                                return true;
                            }
                        }
                        catch (Exception) { }
                    }
                }
                else
                {
                    string fullPath = Path.Combine(basePath, gamePath.Replace("/", "\\"));
                    if (File.Exists(fullPath))
                    {
                        Log.Information($"Found known game executable on disk: {fullPath}");
                        return true;
                    }
                }
            }

            return false;
        }

        public static string[] GetBlacklistedPathTexts() => _blacklist.PathTexts;

        public static string[] GetBlacklistedWords() => _blacklist.DescriptionWords;

        public static List<GameEntry> GetGameList()
        {
            return _gamesList.Select(game => new GameEntry
            {
                Name = game.Name,
                Executables = game.Executables.Select(exe => exe.Replace("/", "\\")).ToList()
            }).ToList();
        }

        private static Regex GetWildcardRegex(string pattern, bool anchorToFullString)
        {
            string cacheKey = (anchorToFullString ? "^" : "") + pattern;
            return _wildcardRegexCache.GetOrAdd(cacheKey, _ =>
            {
                string regexPattern = Regex.Escape(pattern).Replace("\\*", ".*");
                if (anchorToFullString)
                    regexPattern = "^" + regexPattern + "$";
                return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            });
        }

        private static bool MatchesGamePattern(string normalizedPath, string fileName, string gamePattern)
        {
            bool isWildcard = gamePattern.Contains('*');

            if (gamePattern.Contains('/'))
            {
                if (isWildcard)
                    return GetWildcardRegex(gamePattern, false).IsMatch(normalizedPath);
                return normalizedPath.Contains(gamePattern, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                if (isWildcard)
                    return GetWildcardRegex(gamePattern, true).IsMatch(fileName);
                return fileName.Equals(gamePattern, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void LoadGamesFromJson()
        {
            string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");
            string jsonPath = Path.Combine(appDataDir, "games.json");

            if (!File.Exists(jsonPath))
            {
                Log.Warning("games.json file not found. Game detection from JSON will be disabled.");
                return;
            }

            try
            {
                string jsonContent = File.ReadAllText(jsonPath);
                _gamesList = JsonSerializer.Deserialize<List<GameEntry>>(jsonContent) ?? new List<GameEntry>();

                _gameExePaths.Clear();
                _exeToGameName.Clear();
                _exeToIgdbId.Clear();
                _exeToCoverImageId.Clear();
                _wildcardRegexCache.Clear();

                foreach (var entry in _gamesList)
                {
                    foreach (var exe in entry.Executables)
                    {
                        string normalizedExe = exe.Replace("\\", "/");
                        _gameExePaths.Add(normalizedExe);
                        _exeToGameName[normalizedExe] = entry.Name;

                        if (entry.Igdb?.Id != null)
                        {
                            _exeToIgdbId[normalizedExe] = entry.Igdb.Id;
                        }
                        if (!string.IsNullOrEmpty(entry.Igdb?.CoverImageId))
                        {
                            _exeToCoverImageId[normalizedExe] = entry.Igdb.CoverImageId;
                        }
                    }
                }

                Log.Information($"Loaded {_gamesList.Count} games with {_gameExePaths.Count} executables from games.json");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading games.json");
            }

            _ = MessageService.SendFrontendMessage("GameList", GetGameList());
        }

        private static async Task DownloadBlacklistJsonIfNeededAsync()
        {
            string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");
            Directory.CreateDirectory(appDataDir);

            string jsonPath = Path.Combine(appDataDir, "blacklist.json");
            string cdnUrl = "https://cdn.segra.tv/games/blacklist.json";

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Segra");

                try
                {
                    var headRequest = new HttpRequestMessage(HttpMethod.Head, cdnUrl);
                    var headResponse = await httpClient.SendAsync(headRequest);

                    if (!headResponse.IsSuccessStatusCode)
                    {
                        Log.Error($"Failed to fetch metadata from {cdnUrl}. Status: {headResponse.StatusCode}");
                        return;
                    }

                    DateTimeOffset? remoteLastModified = headResponse.Content.Headers.LastModified;

                    bool shouldDownload = false;

                    if (!File.Exists(jsonPath))
                    {
                        Log.Information("Local blacklist.json not found. Downloading...");
                        shouldDownload = true;
                    }
                    else if (remoteLastModified == null)
                    {
                        Log.Warning("Last-Modified header not found. Downloading blacklist.json anyway.");
                        shouldDownload = true;
                    }
                    else
                    {
                        var localLastModified = File.GetLastWriteTimeUtc(jsonPath);

                        if (localLastModified >= remoteLastModified.Value.UtcDateTime)
                        {
                            Log.Information("Local blacklist.json is up to date. Skipping download.");
                            return;
                        }
                        else
                        {
                            Log.Information("Remote blacklist.json is newer. Downloading new version.");
                            shouldDownload = true;
                        }
                    }

                    if (shouldDownload)
                    {
                        var jsonBytes = await httpClient.GetByteArrayAsync(cdnUrl);
                        await File.WriteAllBytesAsync(jsonPath, jsonBytes);

                        if (remoteLastModified != null)
                        {
                            File.SetLastWriteTimeUtc(jsonPath, remoteLastModified.Value.UtcDateTime);
                        }

                        Log.Information("Blacklist download complete");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error downloading blacklist.json");
                }
            }
        }

        private static void LoadBlacklistFromJson()
        {
            string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");
            string jsonPath = Path.Combine(appDataDir, "blacklist.json");

            if (!File.Exists(jsonPath))
            {
                Log.Warning("blacklist.json file not found.");
                return;
            }

            try
            {
                string jsonContent = File.ReadAllText(jsonPath);
                _blacklist = JsonSerializer.Deserialize<BlacklistEntry>(jsonContent) ?? new BlacklistEntry();

                Log.Information($"Loaded blacklist with {_blacklist.PathTexts.Length} path texts and {_blacklist.DescriptionWords.Length} description words from blacklist.json");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading blacklist.json");
            }
        }

        private static async Task DownloadGamesJsonIfNeededAsync()
        {
            string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");
            Directory.CreateDirectory(appDataDir);

            string jsonPath = Path.Combine(appDataDir, "games.json");
            string cdnUrl = "https://cdn.segra.tv/games/games.json";

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Segra");

                try
                {
                    var headRequest = new HttpRequestMessage(HttpMethod.Head, cdnUrl);
                    var headResponse = await httpClient.SendAsync(headRequest);

                    if (!headResponse.IsSuccessStatusCode)
                    {
                        Log.Error($"Failed to fetch metadata from {cdnUrl}. Status: {headResponse.StatusCode}");
                        return;
                    }

                    DateTimeOffset? remoteLastModified = headResponse.Content.Headers.LastModified;

                    bool shouldDownload = false;

                    if (!File.Exists(jsonPath))
                    {
                        Log.Information("Local games.json not found. Downloading...");
                        shouldDownload = true;
                    }
                    else if (remoteLastModified == null)
                    {
                        Log.Warning("Last-Modified header not found. Downloading games.json anyway.");
                        shouldDownload = true;
                    }
                    else
                    {
                        var localLastModified = File.GetLastWriteTimeUtc(jsonPath);

                        if (localLastModified >= remoteLastModified.Value.UtcDateTime)
                        {
                            Log.Information("Local games.json is up to date. Skipping download.");
                            return;
                        }
                        else
                        {
                            Log.Information("Remote games.json is newer. Downloading new version.");
                            shouldDownload = true;
                        }
                    }

                    if (shouldDownload)
                    {
                        var jsonBytes = await httpClient.GetByteArrayAsync(cdnUrl);
                        await File.WriteAllBytesAsync(jsonPath, jsonBytes);

                        if (remoteLastModified != null)
                        {
                            File.SetLastWriteTimeUtc(jsonPath, remoteLastModified.Value.UtcDateTime);
                        }

                        Log.Information("Download complete");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error downloading games.json");
                }
            }
        }

        public class GameEntry
        {
            [JsonPropertyName("name")]
            public required string Name { get; set; }

            [JsonPropertyName("executables")]
            public required List<string> Executables { get; set; }

            [JsonPropertyName("igdb")]
            public IgdbInfo? Igdb { get; set; }
        }

        public class IgdbInfo
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("cover_image_id")]
            public string? CoverImageId { get; set; }
        }

        public class BlacklistEntry
        {
            [JsonPropertyName("path_texts")]
            public string[] PathTexts { get; set; } = [];

            [JsonPropertyName("description_words")]
            public string[] DescriptionWords { get; set; } = [];
        }
    }
}
