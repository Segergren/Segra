using System.Text.Json;
using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Media;
using Segra.Backend.Shared;
using Segra.Backend.Windows.Storage;
using Serilog;

namespace Segra.Backend.Services;

internal static class MigrationService
{
    private record Migration(string Id, Action Apply);

    private static string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");
    private static string MigrationsFolder = Path.Combine(appDataDir, ".migrations");
    private static string AppliedPath => Path.Combine(MigrationsFolder, "applied.json");

    // Signal for when migrations are complete
    private static readonly TaskCompletionSource<bool> _migrationsComplete = new();
    public static Task WaitForMigrationsAsync() => _migrationsComplete.Task;
    public static bool IsRunning { get; private set; } = false;
    public static string? CurrentMigration { get; private set; } = null;

    private static void UpdateMigrationStatus(bool isRunning, string? currentMigration = null)
    {
        IsRunning = isRunning;
        CurrentMigration = currentMigration;
        if (!Program.IsFirstRun)
        {
            _ = MessageService.SendFrontendMessage("MigrationStatus", new { isRunning, currentMigration });
        }
    }

    public static void RunMigrations()
    {
        try
        {
            EnsureStateStorage();

            var applied = LoadApplied();
            var migrations = GetMigrations();

            var pendingMigrations = migrations.Where(m => !applied.Contains(m.Id)).ToList();

            if (pendingMigrations.Count == 0)
            {
                Log.Information("No pending migrations to apply");
                return;
            }

            // Signal that migrations are starting
            UpdateMigrationStatus(true, pendingMigrations.First().Id);

            foreach (var migration in pendingMigrations)
            {
                try
                {
                    Log.Information("Applying migration: {MigrationId}", migration.Id);
                    UpdateMigrationStatus(true, migration.Id);
                    migration.Apply();
                    applied.Add(migration.Id);
                    SaveApplied(applied);
                    Log.Information("Migration completed: {MigrationId}", migration.Id);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Migration failed: {MigrationId}", migration.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RunMigrations encountered an error");
        }
        finally
        {
            UpdateMigrationStatus(false);
            _migrationsComplete.TrySetResult(true);
            Log.Information("All migrations processed");
        }
    }

    private static HashSet<string> LoadApplied()
    {
        try
        {
            if (!File.Exists(AppliedPath)) return new HashSet<string>();
            var json = File.ReadAllText(AppliedPath);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("applied", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                        set.Add(el.GetString() ?? string.Empty);
                }
            }
            return set;
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    private static void SaveApplied(HashSet<string> applied)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new { applied = applied.ToArray() });
            File.WriteAllText(AppliedPath, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed saving applied migrations state");
        }
    }

    private static void EnsureStateStorage()
    {
        try
        {
            if (!Directory.Exists(MigrationsFolder))
            {
                var dir = Directory.CreateDirectory(MigrationsFolder);
                try { dir.Attributes |= FileAttributes.Hidden; } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed ensuring migrations folder");
        }
    }

    private static List<Migration> GetMigrations()
    {
        return new List<Migration>
        {
            new("0001_waveforms_json", Apply_0001_WaveformsJson),
            new("0002_hide_dotfolders", Apply_0002_HideDotfolders),
            new("0003_delete_legacy_games_files", Apply_0003_DeleteLegacyGamesFiles),
            new("0004_game_path_to_paths", Apply_0004_GamePathToPaths),
            new("0005_clip_cpu_defaults", Apply_0005_ClipCpuDefaults),
            new("0006_organize_files_by_game", Apply_0006_OrganizeFilesByGame),
            new("0007_rename_video_folders", Apply_0007_RenameVideoFolders),
            new("0008_move_metadata_to_appdata", Apply_0008_MoveMetadataToAppData)
        };
    }

    // Migration 0001: Remove legacy .audio folder and generate waveform JSONs for existing content
    private static void Apply_0001_WaveformsJson()
    {
        string contentRoot = Settings.Instance.ContentFolder;

        // 1) Remove the legacy .audio folder if present
        try
        {
            string audioFolder = Path.Combine(contentRoot, ".audio");
            if (Directory.Exists(audioFolder))
            {
                Log.Information("Deleting legacy audio folder: {Path}", audioFolder);
                Directory.Delete(audioFolder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete legacy .audio folder");
        }

        // 2) Generate waveform JSONs for each mp4 if missing
        foreach (Content.ContentType type in Enum.GetValues(typeof(Content.ContentType)))
        {
            string typeFolder = Path.Combine(contentRoot, type.ToString().ToLower() + "s");
            if (!Directory.Exists(typeFolder)) continue;

            string targetWaveformFolder = Path.Combine(contentRoot, ".waveforms", type.ToString().ToLower() + "s");
            if (!Directory.Exists(targetWaveformFolder))
            {
                var dir = Directory.CreateDirectory(targetWaveformFolder);
                try { dir.Attributes |= FileAttributes.Hidden; } catch { /* ignore */ }
            }

            foreach (var mp4 in Directory.EnumerateFiles(typeFolder, "*.mp4", SearchOption.AllDirectories))
            {
                try
                {
                    string name = Path.GetFileNameWithoutExtension(mp4);
                    string jsonPath = Path.Combine(targetWaveformFolder, name + ".peaks.json");
                    if (File.Exists(jsonPath))
                    {
                        Log.Debug("Waveform already exists, skipping: {Path}", jsonPath);
                        continue;
                    }

                    Log.Information("Generating waveform for: {File}", mp4);
                    _ = Task.Run(async () => await ContentService.CreateWaveformFile(mp4, type));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed generating waveform for file in migration: {File}", mp4);
                }
            }
        }
    }

    // Migration 0002: Mark all top-level dotfolders under content root as Hidden
    private static void Apply_0002_HideDotfolders()
    {
        string contentRoot = Settings.Instance.ContentFolder;
        if (!Directory.Exists(contentRoot)) return;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(contentRoot, ".*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var di = new DirectoryInfo(dir);
                    // Only mark hidden if it starts with '.'
                    if (di.Name.StartsWith('.'))
                    {
                        di.Attributes |= FileAttributes.Hidden;
                        Log.Information("Ensured hidden attribute on folder: {Folder}", di.FullName);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to set hidden attribute on folder: {Folder}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error while enumerating dotfolders for hidden attribute");
        }
    }

    // Migration 0003: Delete legacy games.json and games.hash files from AppData
    private static void Apply_0003_DeleteLegacyGamesFiles()
    {
        try
        {
            string gamesHashPath = Path.Combine(appDataDir, "games.hash");
            string gamesJsonPath = Path.Combine(appDataDir, "games.json");

            // Only proceed if games.hash exists
            if (File.Exists(gamesHashPath))
            {
                Log.Information("Deleting legacy games.hash file: {Path}", gamesHashPath);
                File.Delete(gamesHashPath);

                if (File.Exists(gamesJsonPath))
                {
                    Log.Information("Deleting legacy games.json file: {Path}", gamesJsonPath);
                    File.Delete(gamesJsonPath);
                }
            }
            else
            {
                Log.Debug("No legacy games files found to delete");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete legacy games files");
        }
    }

    // Migration 0004: Convert Game.path to Game.paths array
    private static void Apply_0004_GamePathToPaths()
    {
        try
        {
            bool needsSave = false;

            // Migrate whitelist
            foreach (var game in Settings.Instance.Whitelist)
            {
                if (game.Paths.Count == 0 && !string.IsNullOrEmpty(game.Path))
                {
                    game.Paths.Add(game.Path);
                    game.Path = string.Empty;
                    needsSave = true;
                    Log.Information("Migrated whitelist game '{Name}' from path to paths", game.Name);
                }
            }

            // Migrate blacklist
            foreach (var game in Settings.Instance.Blacklist)
            {
                if (game.Paths.Count == 0 && !string.IsNullOrEmpty(game.Path))
                {
                    game.Paths.Add(game.Path);
                    game.Path = string.Empty;
                    needsSave = true;
                    Log.Information("Migrated blacklist game '{Name}' from path to paths", game.Name);
                }
            }

            if (needsSave)
            {
                SettingsService.SaveSettings();
                Log.Information("Game path to paths migration completed");
            }
            else
            {
                Log.Debug("No games needed migration from path to paths");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to migrate game paths");
        }
    }

    // Migration 0005: Update clip settings to use CPU encoder by default instead of GPU
    private static void Apply_0005_ClipCpuDefaults()
    {
        try
        {
            bool needsSave = false;
            var settings = Settings.Instance;

            // Migrate if user has old GPU-based quality presets (low, standard, or high)
            // Old presets used GPU encoder with vendor-specific presets

            string qualityPreset = settings.ClipQualityPreset.ToLower();
            bool isOldQualityPreset = qualityPreset == "low" || qualityPreset == "standard" || qualityPreset == "high";
            bool isUsingGpuEncoder = settings.ClipEncoder.Equals("gpu", StringComparison.OrdinalIgnoreCase);

            if (isOldQualityPreset && isUsingGpuEncoder)
            {
                Log.Information("Migrating clip quality preset '{Preset}' from GPU to CPU encoder", settings.ClipQualityPreset);

                // Switch to CPU encoder - keep the same quality preset (low/standard/high)
                // The preset will now apply CPU-specific settings instead of GPU settings
                settings.ClipEncoder = "cpu";

                // Apply appropriate CPU settings based on the quality preset
                switch (qualityPreset)
                {
                    case "low":
                        settings.ClipQualityCpu = 28;
                        settings.ClipAudioQuality = "96k";
                        settings.ClipPreset = "ultrafast";
                        settings.ClipFps = 30;
                        break;
                    case "standard":
                        settings.ClipQualityCpu = 23;
                        settings.ClipAudioQuality = "128k";
                        settings.ClipPreset = "veryfast";
                        settings.ClipFps = 60;
                        break;
                    case "high":
                        settings.ClipQualityCpu = 20;
                        settings.ClipAudioQuality = "192k";
                        settings.ClipPreset = "medium";
                        settings.ClipFps = 60;
                        break;
                }

                needsSave = true;
                Log.Information("Clip settings migrated to CPU: qualityPreset={Preset}, encoder=cpu, qualityCpu={Quality}, audioQuality={Audio}, preset={Preset}, fps={Fps}",
                    settings.ClipQualityPreset, settings.ClipQualityCpu, settings.ClipAudioQuality, settings.ClipPreset, settings.ClipFps);
            }
            else
            {
                Log.Debug("Clip settings don't match old defaults, skipping migration");
            }

            if (needsSave)
            {
                SettingsService.SaveSettings();
                Log.Information("Clip CPU defaults migration completed");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to migrate clip settings to CPU defaults");
        }
    }

    // Migration 0006: Organize existing video files by game
    // Moves files from flat structure (sessions/file.mp4) to game-based structure (sessions/GameName/file.mp4)
    private static void Apply_0006_OrganizeFilesByGame()
    {
        try
        {
            string contentRoot = Settings.Instance.ContentFolder;
            string metadataRoot = Path.Combine(contentRoot, ".metadata");

            if (!Directory.Exists(metadataRoot))
            {
                Log.Information("No metadata folder found, skipping file organization migration");
                return;
            }

            int movedCount = 0;
            int errorCount = 0;

            // Process each content type
            foreach (Content.ContentType type in Enum.GetValues(typeof(Content.ContentType)))
            {
                string typeName = type.ToString().ToLower() + "s";
                string metadataFolder = Path.Combine(metadataRoot, typeName);
                string videoFolder = Path.Combine(contentRoot, typeName);

                if (!Directory.Exists(metadataFolder))
                {
                    Log.Debug("No metadata folder for {Type}, skipping", typeName);
                    continue;
                }

                foreach (var metadataFilePath in Directory.EnumerateFiles(metadataFolder, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        // Read metadata to get game name and current file path
                        string metadataJson = File.ReadAllText(metadataFilePath);
                        var metadata = JsonSerializer.Deserialize<Content>(metadataJson);

                        if (metadata == null)
                        {
                            Log.Warning("Failed to deserialize metadata: {Path}", metadataFilePath);
                            continue;
                        }

                        string currentFilePath = metadata.FilePath;

                        // Skip if file doesn't exist or path is empty
                        if (string.IsNullOrEmpty(currentFilePath) || !File.Exists(currentFilePath))
                        {
                            Log.Debug("Video file not found or path empty for metadata: {Path}", metadataFilePath);
                            continue;
                        }

                        // Skip if already in a game subfolder
                        string currentDir = Path.GetDirectoryName(currentFilePath) ?? "";
                        string expectedFlatDir = videoFolder.Replace("\\", "/");
                        string actualDir = currentDir.Replace("\\", "/");

                        // Check if file is already in a subfolder (not directly in sessions/buffers/clips/highlights)
                        if (!actualDir.Equals(expectedFlatDir, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Debug("File already in subfolder, skipping: {Path}", currentFilePath);
                            continue;
                        }

                        // Get sanitized game name for folder
                        string gameName = metadata.Game ?? "Unknown";
                        string sanitizedGameName = StorageService.SanitizeGameNameForFolder(gameName);

                        // Calculate new path
                        string fileName = Path.GetFileName(currentFilePath);
                        string newDir = Path.Combine(videoFolder, sanitizedGameName);
                        string newFilePath = Path.Combine(newDir, fileName);

                        // Create directory if needed
                        if (!Directory.Exists(newDir))
                        {
                            Directory.CreateDirectory(newDir);
                            Log.Information("Created game folder: {Folder}", newDir);
                        }

                        // Move the file
                        Log.Information("Moving {OldPath} to {NewPath}", currentFilePath, newFilePath);
                        File.Move(currentFilePath, newFilePath);

                        // Update metadata with new file path
                        metadata.FilePath = newFilePath;
                        string updatedMetadataJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(metadataFilePath, updatedMetadataJson);

                        movedCount++;
                        Log.Information("Successfully migrated: {FileName} -> {GameFolder}", fileName, sanitizedGameName);
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        Log.Error(ex, "Failed to migrate file for metadata: {Path}", metadataFilePath);
                    }
                }
            }

            Log.Information("File organization migration completed. Moved: {Moved}, Errors: {Errors}", movedCount, errorCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to organize files by game");
        }
    }

    // Migration 0007: Rename video folders from legacy names to new names
    // sessions -> Full Sessions, buffers -> Replay Buffers, clips -> Clips, highlights -> Highlights
    private static void Apply_0007_RenameVideoFolders()
    {
        try
        {
            string contentRoot = Settings.Instance.ContentFolder;
            if (!Directory.Exists(contentRoot))
            {
                Log.Information("Content folder does not exist, skipping video folder rename migration");
                return;
            }

            int renamedCount = 0;
            int errorCount = 0;

            // Define the folder renames (legacy name -> new name)
            var folderRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { FolderNames.LegacySessions, FolderNames.Sessions },
                { FolderNames.LegacyBuffers, FolderNames.Buffers },
                { FolderNames.LegacyClips, FolderNames.Clips },
                { FolderNames.LegacyHighlights, FolderNames.Highlights }
            };

            // Get actual folder names from the file system to handle case-sensitivity properly
            foreach (var existingDir in Directory.GetDirectories(contentRoot))
            {
                string actualFolderName = Path.GetFileName(existingDir);

                // Check if this folder matches any of our legacy names (case-insensitive)
                if (!folderRenames.TryGetValue(actualFolderName, out string? newFolderName))
                {
                    continue; // Not a folder we need to rename
                }

                // Check if rename is actually needed (case-sensitive comparison)
                if (actualFolderName == newFolderName)
                {
                    Log.Debug("Folder already has correct name: {Path}", existingDir);
                    continue;
                }

                string newPath = Path.Combine(contentRoot, newFolderName);

                try
                {
                    // On Windows, renaming just for case change requires a two-step process
                    // because the file system is case-insensitive
                    if (actualFolderName.Equals(newFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Case-only rename: use temp folder
                        string tempPath = existingDir + "_temp_rename";
                        Log.Information("Case-only rename: {OldPath} -> {TempPath} -> {NewPath}", existingDir, tempPath, newPath);
                        Directory.Move(existingDir, tempPath);
                        Directory.Move(tempPath, newPath);
                    }
                    else
                    {
                        // Different name, direct rename
                        Log.Information("Renaming folder: {OldPath} -> {NewPath}", existingDir, newPath);
                        Directory.Move(existingDir, newPath);
                    }
                    renamedCount++;
                    Log.Information("Successfully renamed folder: {OldName} -> {NewName}", actualFolderName, newFolderName);
                }
                catch (Exception ex)
                {
                    errorCount++;
                    Log.Error(ex, "Failed to rename folder: {OldPath} -> {NewPath}", existingDir, newPath);
                }
            }

            // Update metadata files with new file paths
            // Check BOTH old location (.metadata in content folder) AND new location (AppData/Segra/metadata)
            int updatedCount = 0;
            var metadataLocations = new List<(string root, bool useLegacySubfolders)>
            {
                (Path.Combine(contentRoot, FolderNames.LegacyMetadata), true),  // Old location with legacy subfolder names
                (Path.Combine(FolderNames.AppDataFolder, FolderNames.Metadata), false)  // New location with new subfolder names
            };

            foreach (var (metadataRoot, useLegacySubfolders) in metadataLocations)
            {
                if (!Directory.Exists(metadataRoot))
                {
                    Log.Debug("Metadata root does not exist, skipping: {Path}", metadataRoot);
                    continue;
                }

                foreach (Content.ContentType type in Enum.GetValues(typeof(Content.ContentType)))
                {
                    string subfolderName = useLegacySubfolders
                        ? FolderNames.GetLegacyVideoFolderName(type)
                        : FolderNames.GetVideoFolderName(type);
                    string metadataFolder = Path.Combine(metadataRoot, subfolderName);

                    if (!Directory.Exists(metadataFolder))
                    {
                        Log.Debug("No metadata folder for {Type}, skipping path update", subfolderName);
                        continue;
                    }

                    foreach (var metadataFilePath in Directory.EnumerateFiles(metadataFolder, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            string metadataJson = File.ReadAllText(metadataFilePath);
                            var metadata = JsonSerializer.Deserialize<Content>(metadataJson);

                            if (metadata == null || string.IsNullOrEmpty(metadata.FilePath))
                            {
                                continue;
                            }

                            // Update the file path to use new folder names
                            string updatedFilePath = metadata.FilePath;
                            foreach (var rename in folderRenames)
                            {
                                // Replace legacy folder name with new folder name in the path
                                string oldPathSegment = $"\\{rename.Key}\\";
                                string newPathSegment = $"\\{rename.Value}\\";
                                if (updatedFilePath.Contains(oldPathSegment, StringComparison.OrdinalIgnoreCase))
                                {
                                    updatedFilePath = updatedFilePath.Replace(oldPathSegment, newPathSegment, StringComparison.OrdinalIgnoreCase);
                                }
                                // Also handle forward slashes
                                oldPathSegment = $"/{rename.Key}/";
                                newPathSegment = $"/{rename.Value}/";
                                if (updatedFilePath.Contains(oldPathSegment, StringComparison.OrdinalIgnoreCase))
                                {
                                    updatedFilePath = updatedFilePath.Replace(oldPathSegment, newPathSegment, StringComparison.OrdinalIgnoreCase);
                                }
                            }

                            if (updatedFilePath != metadata.FilePath)
                            {
                                metadata.FilePath = updatedFilePath;
                                string updatedMetadataJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                                File.WriteAllText(metadataFilePath, updatedMetadataJson);
                                updatedCount++;
                                Log.Debug("Updated file path in metadata: {Path}", metadataFilePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Failed to update metadata file path: {Path}", metadataFilePath);
                        }
                    }
                }
            }
            Log.Information("Updated {Count} metadata files with new folder paths", updatedCount);

            Log.Information("Video folder rename migration completed. Renamed: {Renamed}, Errors: {Errors}", renamedCount, errorCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to rename video folders");
        }
    }

    // Migration 0008: Move metadata, thumbnails, and waveforms to AppData and make them visible
    // .metadata, .thumbnails, .waveforms -> AppData/Roaming/Segra/metadata, thumbnails, waveforms
    private static void Apply_0008_MoveMetadataToAppData()
    {
        try
        {
            string contentRoot = Settings.Instance.ContentFolder;
            string appDataRoot = FolderNames.AppDataFolder;

            if (!Directory.Exists(contentRoot))
            {
                Log.Information("Content folder does not exist, skipping metadata move migration");
                return;
            }

            // Ensure AppData folder exists
            if (!Directory.Exists(appDataRoot))
            {
                Directory.CreateDirectory(appDataRoot);
                Log.Information("Created AppData folder: {Path}", appDataRoot);
            }

            int movedCount = 0;
            int errorCount = 0;

            // Remove .ai folder if it exists (not used in the application)
            string aiFolder = Path.Combine(contentRoot, ".ai");
            if (Directory.Exists(aiFolder))
            {
                try
                {
                    Directory.Delete(aiFolder, true);
                    Log.Information("Deleted unused .ai folder: {Path}", aiFolder);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete .ai folder: {Path}", aiFolder);
                }
            }

            // Define the folder moves (source in contentFolder -> destination in AppData)
            var folderMoves = new Dictionary<string, string>
            {
                { FolderNames.LegacyMetadata, FolderNames.Metadata },
                { FolderNames.LegacyThumbnails, FolderNames.Thumbnails },
                { FolderNames.LegacyWaveforms, FolderNames.Waveforms }
            };

            foreach (var move in folderMoves)
            {
                string sourcePath = Path.Combine(contentRoot, move.Key);
                string destPath = Path.Combine(appDataRoot, move.Value);

                if (!Directory.Exists(sourcePath))
                {
                    Log.Debug("Source folder does not exist, skipping: {Path}", sourcePath);
                    continue;
                }

                try
                {
                    Log.Information("Moving folder: {Source} -> {Dest}", sourcePath, destPath);

                    // Create destination folder if it doesn't exist
                    if (!Directory.Exists(destPath))
                    {
                        Directory.CreateDirectory(destPath);
                    }

                    // Define subfolder renames (case-insensitive lookup)
                    var subfolderRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { FolderNames.LegacySessions, FolderNames.Sessions },
                        { FolderNames.LegacyBuffers, FolderNames.Buffers },
                        { FolderNames.LegacyClips, FolderNames.Clips },
                        { FolderNames.LegacyHighlights, FolderNames.Highlights }
                    };

                    // Move all subdirectories (content type folders)
                    foreach (var subDir in Directory.GetDirectories(sourcePath))
                    {
                        string subDirName = Path.GetFileName(subDir);
                        string destSubDir = Path.Combine(destPath, subDirName);

                        // Check if this is a legacy subfolder name and needs renaming (case-insensitive)
                        string newSubDirName = subDirName;
                        if (subfolderRenames.TryGetValue(subDirName, out string? mappedName))
                        {
                            newSubDirName = mappedName;
                        }

                        if (newSubDirName != subDirName)
                        {
                            destSubDir = Path.Combine(destPath, newSubDirName);
                            Log.Debug("Renaming subfolder during move: {Old} -> {New}", subDirName, newSubDirName);
                        }

                        if (!Directory.Exists(destSubDir))
                        {
                            Directory.CreateDirectory(destSubDir);
                        }

                        // Move all files from source subfolder to destination subfolder
                        foreach (var file in Directory.GetFiles(subDir))
                        {
                            string fileName = Path.GetFileName(file);
                            string destFile = Path.Combine(destSubDir, fileName);

                            try
                            {
                                if (File.Exists(destFile))
                                {
                                    Log.Debug("Destination file already exists, skipping: {Path}", destFile);
                                    continue;
                                }

                                File.Move(file, destFile);
                                movedCount++;
                            }
                            catch (Exception ex)
                            {
                                errorCount++;
                                Log.Error(ex, "Failed to move file: {Source} -> {Dest}", file, destFile);
                            }
                        }
                    }

                    // Also move any files directly in the source folder (shouldn't be many)
                    foreach (var file in Directory.GetFiles(sourcePath))
                    {
                        string fileName = Path.GetFileName(file);
                        string destFile = Path.Combine(destPath, fileName);

                        try
                        {
                            if (File.Exists(destFile))
                            {
                                Log.Debug("Destination file already exists, skipping: {Path}", destFile);
                                continue;
                            }

                            File.Move(file, destFile);
                            movedCount++;
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            Log.Error(ex, "Failed to move file: {Source} -> {Dest}", file, destFile);
                        }
                    }

                    // Try to delete the old folder if empty
                    try
                    {
                        if (Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories).Length == 0)
                        {
                            Directory.Delete(sourcePath, true);
                            Log.Information("Deleted empty source folder: {Path}", sourcePath);
                        }
                        else
                        {
                            Log.Warning("Source folder not empty after move, keeping: {Path}", sourcePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to delete source folder: {Path}", sourcePath);
                    }
                }
                catch (Exception ex)
                {
                    errorCount++;
                    Log.Error(ex, "Failed to move folder: {Source} -> {Dest}", sourcePath, destPath);
                }
            }

            // Remove hidden attribute from AppData folders
            try
            {
                var foldersToUnhide = new[]
                {
                    Path.Combine(appDataRoot, FolderNames.Metadata),
                    Path.Combine(appDataRoot, FolderNames.Thumbnails),
                    Path.Combine(appDataRoot, FolderNames.Waveforms)
                };

                foreach (var folder in foldersToUnhide)
                {
                    if (Directory.Exists(folder))
                    {
                        var di = new DirectoryInfo(folder);
                        if ((di.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                        {
                            di.Attributes &= ~FileAttributes.Hidden;
                            Log.Information("Removed hidden attribute from folder: {Path}", folder);
                        }

                        // Also unhide all subfolders
                        foreach (var subDir in Directory.GetDirectories(folder))
                        {
                            var subDi = new DirectoryInfo(subDir);
                            if ((subDi.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                            {
                                subDi.Attributes &= ~FileAttributes.Hidden;
                                Log.Debug("Removed hidden attribute from subfolder: {Path}", subDir);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error removing hidden attributes from folders");
            }

            SettingsService.LoadContentFromFolderIntoState().GetAwaiter().GetResult();
            Log.Information("Metadata move migration completed. Moved files: {Moved}, Errors: {Errors}", movedCount, errorCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to move metadata to AppData");
        }
    }
}
