using Segra.Backend.Core.Models;
using Segra.Backend.Media;
using Serilog;

namespace Segra.Backend.Services;

internal static class MigrationService
{
    private record Migration(string Id, Action Apply);

    private static string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");
    private static string MigrationsFolder = Path.Combine(appDataDir, ".migrations");
    private static string AppliedPath => Path.Combine(MigrationsFolder, "applied.json");

    public static void RunMigrations()
    {
        try
        {
            EnsureStateStorage();

            var applied = LoadApplied();
            var migrations = GetMigrations();

            foreach (var migration in migrations)
            {
                if (applied.Contains(migration.Id))
                {
                    Log.Information("Migration already applied: {MigrationId}", migration.Id);
                    continue;
                }

                try
                {
                    Log.Information("Applying migration: {MigrationId}", migration.Id);
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
            new("0004_game_path_to_paths", Apply_0004_GamePathToPaths)
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

            foreach (var mp4 in Directory.EnumerateFiles(typeFolder, "*.mp4", SearchOption.TopDirectoryOnly))
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
}
