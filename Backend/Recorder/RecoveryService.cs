using Serilog;
using System.Text.Json;
using Segra.Backend.App;
using Segra.Backend.Core;
using Segra.Backend.Media;
using Segra.Backend.Shared;
using Segra.Backend.Core.Models;
using Segra.Backend.Windows.Storage;

namespace Segra.Backend.Recorder
{
    internal class RecoveryService
    {
        private static readonly Dictionary<string, OrphanedFile> _pendingRecoveries = new();
        private static readonly Dictionary<string, string> _detectedGames = new();

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

                var fileDataList = orphanedFiles.Select(orphanedFile =>
                {
                    string recoveryId = Guid.NewGuid().ToString();
                    _pendingRecoveries[recoveryId] = orphanedFile;

                    FileInfo fileInfo = new(orphanedFile.FilePath);
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

                    string? detectedGame = orphanedFile.FolderGame;
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
                _detectedGames.TryGetValue(recoveryId, out string? detectedGame);
                _detectedGames.Remove(recoveryId);

                switch (action)
                {
                    case "recover":
                        string? gameName = string.IsNullOrEmpty(gameOverride) ? detectedGame : gameOverride;
                        await RecoverFile(orphanedFile, gameName);
                        await SettingsService.LoadContentFromFolderIntoState(true);
                        break;
                    case "delete":
                        DeleteFile(orphanedFile);
                        break;
                    case "skip":
                        Log.Information($"User skipped recovery for: {orphanedFile.FileName}");
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
            var knownGameNames = AppState.Instance.Content
                .Where(c => !string.IsNullOrEmpty(c.Game))
                .Select(c => c.Game)
                .Concat(Settings.Instance.Games.Select(g => g.Name))
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
                string videoFolder = PathUtils.Combine(contentFolder, FolderNames.GetVideoFolderName(type));
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
            private string _filePath = string.Empty;
            public required string FilePath
            {
                get => _filePath;
                set => _filePath = PathUtils.Normalize(value ?? string.Empty);
            }
            public required Content.ContentType Type { get; set; }
            public required string FileName { get; set; }
            public string? FolderGame { get; set; }
        }
    }
}
