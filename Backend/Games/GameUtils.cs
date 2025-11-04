using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Segra.Backend.Games
{
    public static class GameUtils
    {
        private static HashSet<string> _gameExePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _exeToGameName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _isInitialized = false;

        public static async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await DownloadGamesJsonIfNeededAsync();
            LoadGamesFromJson();
            _isInitialized = true;
        }

        public static bool IsGameExePath(string exePath)
        {
            if (!_isInitialized || string.IsNullOrEmpty(exePath))
                return false;

            string normalizedPath = exePath.Replace("\\", "/");
            string fileName = Path.GetFileName(exePath);

            // Check if any game exe path matches
            foreach (var gamePath in _gameExePaths)
            {
                // If gamePath contains a slash, it's a path - check if it's contained in the full path
                if (gamePath.Contains('/'))
                {
                    if (normalizedPath.Contains(gamePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                // Otherwise it's just a filename - check exact match
                else
                {
                    if (fileName.Equals(gamePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
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
                // If the key contains a slash, it's a path - check if it's contained in the full path
                if (entry.Key.Contains('/'))
                {
                    if (normalizedPath.Contains(entry.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Value;
                    }
                }
                // Otherwise it's just a filename - check exact match
                else
                {
                    if (fileName.Equals(entry.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Value;
                    }
                }
            }

            return null;
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
                var gamesList = JsonSerializer.Deserialize<List<GameEntry>>(jsonContent) ?? new List<GameEntry>();

                // Build lookup collections for fast access
                _gameExePaths.Clear();
                _exeToGameName.Clear();

                foreach (var entry in gamesList)
                {
                    foreach (var exe in entry.Executables)
                    {
                        // Normalize path for consistent comparison
                        string normalizedExe = exe.Replace("\\", "/");
                        _gameExePaths.Add(normalizedExe);
                        _exeToGameName[normalizedExe] = entry.Name;
                    }
                }

                Log.Information($"Loaded {gamesList.Count} games with {_gameExePaths.Count} executables from games.json");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading games.json");
            }
        }

        private static async Task DownloadGamesJsonIfNeededAsync()
        {
            string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");
            Directory.CreateDirectory(appDataDir); // Ensure directory exists

            string jsonPath = Path.Combine(appDataDir, "games.json");
            string cdnUrl = "https://cdn.segra.tv/games/games.json";

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Segra");

                try
                {
                    // Send HEAD request to check Last-Modified header
                    var headRequest = new HttpRequestMessage(HttpMethod.Head, cdnUrl);
                    var headResponse = await httpClient.SendAsync(headRequest);

                    if (!headResponse.IsSuccessStatusCode)
                    {
                        Log.Error($"Failed to fetch metadata from {cdnUrl}. Status: {headResponse.StatusCode}");
                        return;
                    }

                    DateTimeOffset? remoteLastModified = headResponse.Content.Headers.LastModified;

                    // Check if we need to download
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
                        // Compare remote Last-Modified with local file's last write time
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

                        // Set the file's last write time to match the remote Last-Modified timestamp
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

        private class GameEntry
        {
            [JsonPropertyName("name")]
            public required string Name { get; set; }

            [JsonPropertyName("executables")]
            public required List<string> Executables { get; set; }
        }
    }
}
