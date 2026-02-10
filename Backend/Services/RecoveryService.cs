using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Games;
using Segra.Backend.Media;
using Segra.Backend.Shared;
using Segra.Backend.Windows.Storage;
using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Segra.Backend.Services
{
    internal class AiIdentificationResponse
    {
        [JsonPropertyName("game_name")]
        public string? GameName { get; set; }

        [JsonPropertyName("ai_confidence")]
        public double AiConfidence { get; set; }

        [JsonPropertyName("game_name_confidence")]
        public double? GameNameConfidence { get; set; }
    }

    internal class RecoveryService
    {
        private static readonly Dictionary<string, OrphanedFile> _pendingRecoveries = new();
        private static readonly Dictionary<string, string> _detectedGames = new();

        private static async Task<Dictionary<string, string>> IdentifyGamesWithAi(List<OrphanedFile> orphanedFiles)
        {
            var identifiedGames = new Dictionary<string, string>();

            try
            {
                var playedGames = Settings.Instance.State.Content
                    .Where(c => !string.IsNullOrEmpty(c.Game))
                    .Select(c => c.Game)
                    .Distinct()
                    .ToList();

                if (playedGames.Count == 0)
                {
                    Log.Information("No previously played games found, skipping AI identification");
                    return identifiedGames;
                }

                string jwt = await AuthService.GetJwtAsync();
                if (string.IsNullOrEmpty(jwt))
                {
                    Log.Warning("Failed to get JWT for AI identification");
                    return identifiedGames;
                }

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                var semaphore = new SemaphoreSlim(10, 10);
                var tasks = orphanedFiles.Select(async orphanedFile =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var identifiedGame = await IdentifySingleGameWithAi(httpClient, orphanedFile.FilePath, playedGames);
                        if (!string.IsNullOrEmpty(identifiedGame))
                        {
                            lock (identifiedGames)
                            {
                                identifiedGames[orphanedFile.FilePath] = identifiedGame;
                            }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
                Log.Information($"AI identified {identifiedGames.Count} out of {orphanedFiles.Count} orphaned files");
            }
            catch (Exception ex)
            {
                Log.Error($"Error during AI game identification: {ex.Message}");
            }

            return identifiedGames;
        }

        private static async Task<string?> IdentifySingleGameWithAi(HttpClient httpClient, string videoFilePath, List<string> playedGames)
        {
            try
            {
                var duration = await FFmpegService.GetVideoDuration(videoFilePath);
                double thumbnailTime = duration.TotalSeconds > 0 ? Math.Min(duration.TotalSeconds * 0.1, 5) : 0;

                byte[] thumbnailBytes = await FFmpegService.GenerateThumbnail(videoFilePath, thumbnailTime, 720);
                if (thumbnailBytes == null || thumbnailBytes.Length == 0)
                {
                    Log.Warning($"Failed to generate thumbnail for {Path.GetFileName(videoFilePath)}");
                    return null;
                }

                using var content = new MultipartFormDataContent();
                var imageContent = new ByteArrayContent(thumbnailBytes);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                content.Add(imageContent, "file", "thumbnail.jpg");

                var gamesJson = JsonSerializer.Serialize(playedGames);
                content.Add(new StringContent(gamesJson), "games");

                var response = await httpClient.PostAsync("https://processing.segra.tv/ai/identify-game", content);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning($"AI identification failed for {Path.GetFileName(videoFilePath)}: {response.StatusCode}");
                    return null;
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var aiResponse = JsonSerializer.Deserialize<AiIdentificationResponse>(responseJson);

                if (aiResponse?.GameName != null && aiResponse.AiConfidence > 0)
                {
                    Log.Information($"AI identified {Path.GetFileName(videoFilePath)} as '{aiResponse.GameName}' (confidence: {aiResponse.AiConfidence})");
                    return aiResponse.GameName;
                }

                return null;
            }
            catch (TaskCanceledException)
            {
                Log.Warning($"AI identification timed out for {Path.GetFileName(videoFilePath)}");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error($"Error identifying game for {Path.GetFileName(videoFilePath)}: {ex.Message}");
                return null;
            }
        }

        public static async Task CheckForOrphanedFilesAsync()
        {
            try
            {
                // Wait for migrations to complete before checking for orphaned files
                await MigrationService.WaitForMigrationsAsync();
                var orphanedFiles = FindOrphanedVideoFiles();

                if (orphanedFiles.Count == 0)
                    return;

                Log.Information($"Found {orphanedFiles.Count} orphaned video file(s) without metadata");

                // Only use AI for files that aren't already in a game subfolder
                var filesNeedingAi = orphanedFiles.Where(f => string.IsNullOrEmpty(f.FolderGame)).ToList();
                Dictionary<string, string>? aiIdentifiedGames = null;

                if (filesNeedingAi.Count > 0)
                {
                    bool useAi = Settings.Instance.EnableAi && AuthService.IsAuthenticated();
                    if (useAi)
                    {
                        aiIdentifiedGames = await IdentifyGamesWithAi(filesNeedingAi);
                    }
                }

                var fileDataList = orphanedFiles.Select(orphanedFile =>
                {
                    string recoveryId = Guid.NewGuid().ToString();
                    _pendingRecoveries[recoveryId] = orphanedFile;

                    FileInfo fileInfo = new FileInfo(orphanedFile.FilePath);
                    double fileSizeMB = fileInfo.Length / (1024.0 * 1024.0);
                    string formattedSize = fileSizeMB >= 1024
                        ? $"{fileSizeMB / 1024.0:F2} GB"
                        : $"{fileSizeMB:F2} MB";

                    string typeLabel = orphanedFile.Type switch
                    {
                        Content.ContentType.Session => "Session Recording",
                        Content.ContentType.Clip => "Clip",
                        Content.ContentType.Highlight => "Highlight",
                        Content.ContentType.Buffer => "Replay Buffer",
                        _ => orphanedFile.Type.ToString()
                    };

                    // Use folder-derived game first, then AI as fallback
                    string? detectedGame = orphanedFile.FolderGame;
                    if (string.IsNullOrEmpty(detectedGame) && aiIdentifiedGames != null && aiIdentifiedGames.TryGetValue(orphanedFile.FilePath, out string? aiGame))
                    {
                        detectedGame = aiGame;
                    }

                    if (!string.IsNullOrEmpty(detectedGame))
                    {
                        _detectedGames[recoveryId] = detectedGame;
                    }

                    return new
                    {
                        recoveryId,
                        fileName = orphanedFile.FileName,
                        filePath = orphanedFile.FilePath,
                        type = orphanedFile.Type.ToString(),
                        typeLabel,
                        fileSize = formattedSize,
                        detectedGame
                    };
                }).ToList();

                await MessageService.SendFrontendMessage("RecoveryPrompt", new
                {
                    files = fileDataList,
                    totalCount = fileDataList.Count
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Error during recovery check: {ex.Message}");
            }
        }

        public static async Task HandleRecoveryConfirm(JsonElement parameters)
        {
            try
            {
                if (!parameters.TryGetProperty("recoveryId", out JsonElement recoveryIdElement) ||
                    !parameters.TryGetProperty("action", out JsonElement actionElement))
                {
                    Log.Error("Missing required parameters in RecoveryConfirm");
                    return;
                }

                string recoveryId = recoveryIdElement.GetString()!;
                string action = actionElement.GetString()!;
                string? gameOverride = parameters.TryGetProperty("gameOverride", out JsonElement gameOverrideElement)
                    ? gameOverrideElement.GetString()
                    : null;

                if (!_pendingRecoveries.TryGetValue(recoveryId, out OrphanedFile? orphanedFile))
                {
                    Log.Warning($"No pending recovery found for recoveryId: {recoveryId}");
                    return;
                }

                _pendingRecoveries.Remove(recoveryId);

                switch (action)
                {
                    case "recover":
                        string? gameName = gameOverride;
                        if (string.IsNullOrEmpty(gameName))
                        {
                            gameName = _detectedGames.TryGetValue(recoveryId, out string? detected) ? detected : null;
                        }
                        await RecoverFile(orphanedFile, gameName);
                        _detectedGames.Remove(recoveryId);
                        await SettingsService.LoadContentFromFolderIntoState(true);
                        break;
                    case "delete":
                        DeleteFile(orphanedFile);
                        _detectedGames.Remove(recoveryId);
                        break;
                    case "skip":
                        Log.Information($"User skipped recovery for: {orphanedFile.FileName}");
                        _detectedGames.Remove(recoveryId);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error handling recovery confirmation: {ex.Message}");
            }
        }

        private static List<OrphanedFile> FindOrphanedVideoFiles()
        {
            var orphanedFiles = new List<OrphanedFile>();
            string contentFolder = Settings.Instance.ContentFolder;

            // Build a lookup from sanitized folder name -> original game name
            var knownGameNames = Settings.Instance.State.Content
                .Where(c => !string.IsNullOrEmpty(c.Game))
                .Select(c => c.Game)
                .Concat(Settings.Instance.Whitelist.Select(g => g.Name))
                .Concat(Settings.Instance.Blacklist.Select(g => g.Name))
                .Distinct()
                .ToList();

            var folderToGameName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gameName in knownGameNames)
            {
                string sanitized = StorageService.SanitizeGameNameForFolder(gameName);
                folderToGameName.TryAdd(sanitized, gameName);
            }

            var contentTypes = new[]
            {
                Content.ContentType.Session,
                Content.ContentType.Clip,
                Content.ContentType.Highlight,
                Content.ContentType.Buffer
            };

            foreach (var type in contentTypes)
            {
                string videoFolder = Path.Combine(contentFolder, FolderNames.GetVideoFolderName(type));
                string metadataFolder = FolderNames.GetMetadataFolderPath(type);

                if (!Directory.Exists(videoFolder))
                    continue;

                // Search recursively to find files in game subfolders
                var videoFiles = Directory.GetFiles(videoFolder, "*.mp4", SearchOption.AllDirectories);

                foreach (var videoFile in videoFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(videoFile);
                    string metadataFile = Path.Combine(metadataFolder, $"{fileName}.json");

                    if (!File.Exists(metadataFile))
                    {
                        // Detect game from parent folder name (e.g., "Full Sessions/PUBG/video.mp4" -> "PUBG")
                        string? folderGame = null;
                        string? parentDir = Path.GetDirectoryName(videoFile);
                        if (parentDir != null && !string.Equals(parentDir, videoFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            string folderName = Path.GetFileName(parentDir);
                            // Reverse-lookup: match sanitized folder name back to original game name
                            folderGame = folderToGameName.TryGetValue(folderName, out string? originalName)
                                ? originalName
                                : folderName;
                        }

                        orphanedFiles.Add(new OrphanedFile
                        {
                            FilePath = videoFile,
                            Type = type,
                            FileName = Path.GetFileName(videoFile),
                            FolderGame = folderGame
                        });
                    }
                }
            }

            return orphanedFiles;
        }

        private static async Task RecoverFile(OrphanedFile orphanedFile, string? detectedGame)
        {
            try
            {
                Log.Information($"Recovering file: {orphanedFile.FileName}");

                string gameName;
                if (!string.IsNullOrEmpty(detectedGame))
                {
                    gameName = detectedGame;
                    Log.Information($"Using detected game: {gameName}");
                }
                else
                {
                    gameName = "Unknown";
                }

                DateTime createdAt = File.GetCreationTime(orphanedFile.FilePath);

                await ContentService.CreateMetadataFile(
                    orphanedFile.FilePath,
                    orphanedFile.Type,
                    gameName,
                    null,
                    null,
                    createdAt != DateTime.MinValue ? createdAt : null,
                    igdbId: null
                );

                await ContentService.CreateThumbnail(orphanedFile.FilePath, orphanedFile.Type);
                await ContentService.CreateWaveformFile(orphanedFile.FilePath, orphanedFile.Type);

                Log.Information($"Successfully recovered: {orphanedFile.FileName}");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to recover file {orphanedFile.FileName}: {ex.Message}");
            }
        }

        private static void DeleteFile(OrphanedFile orphanedFile)
        {
            try
            {
                Log.Information($"Deleting orphaned file: {orphanedFile.FileName}");
                File.Delete(orphanedFile.FilePath);
                Log.Information($"Successfully deleted: {orphanedFile.FileName}");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to delete file {orphanedFile.FileName}: {ex.Message}");
            }
        }

        private class OrphanedFile
        {
            public required string FilePath { get; set; }
            public required Content.ContentType Type { get; set; }
            public required string FileName { get; set; }
            public string? FolderGame { get; set; }
        }
    }
}
