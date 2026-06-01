using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Media;
using Segra.Backend.Shared;
using Segra.Backend.Windows.Storage;
using Serilog;

namespace Segra.Backend.Services
{
    /// <summary>
    /// Moves existing content whose video files live outside the selected recording path
    /// (Settings.ContentFolder) into it, one video at a time. Unrelated to the startup schema
    /// <see cref="MigrationService"/>. Sidecars (metadata/thumbnail/waveform) live in the cache
    /// folder keyed by file name, so only the video file moves and its sidecar FilePath is updated.
    /// </summary>
    internal static class ContentMigrationService
    {
        private const string ProgressMessage = "ContentMigrationProgress";

        // Leave the same free-space floor StorageService keeps while recording.
        private const long SafetyMarginBytes = StorageService.MinimumRecordingFreeSpaceBytes;

        private static readonly object _lock = new();
        private static bool _isMigrating;

        public sealed record OutsideContent(Content Content, long SizeBytes, bool CrossDrive);

        public static List<OutsideContent> GetContentOutsideRecordingPath(string contentFolder)
        {
            var result = new List<OutsideContent>();

            if (string.IsNullOrWhiteSpace(contentFolder))
                return result;

            string root = PathUtils.Normalize(contentFolder).TrimEnd('/');
            if (root.Length == 0)
                return result;

            string rootPrefix = root + "/";
            string? destDrive = DriveRoot(contentFolder);

            foreach (var content in AppState.Instance.Content.ToList())
            {
                if (content == null || string.IsNullOrWhiteSpace(content.FilePath))
                    continue;

                string normalized = PathUtils.Normalize(content.FilePath);
                if (normalized.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!File.Exists(content.FilePath))
                    continue;

                long size;
                try
                {
                    size = new FileInfo(content.FilePath).Length;
                }
                catch (Exception ex)
                {
                    Log.Warning("Could not read size of {File}: {Message}", content.FilePath, ex.Message);
                    continue;
                }

                bool crossDrive = !string.Equals(DriveRoot(content.FilePath), destDrive, StringComparison.OrdinalIgnoreCase);
                result.Add(new OutsideContent(content, size, crossDrive));
            }

            return result;
        }

        public static async Task HandleMigrateContent()
        {
            lock (_lock)
            {
                if (_isMigrating)
                {
                    Log.Information("Content migration already in progress, ignoring duplicate request.");
                    return;
                }
                _isMigrating = true;
            }

            string migrationId = Guid.NewGuid().ToString("N");

            try
            {
                string contentFolder = Settings.Instance.ContentFolder;
                if (string.IsNullOrWhiteSpace(contentFolder))
                {
                    await SendProgress(migrationId, string.Empty, 0, "error", 0, 0, "No recording path is configured.");
                    return;
                }

                var outside = GetContentOutsideRecordingPath(contentFolder);
                if (outside.Count == 0)
                {
                    await SendProgress(migrationId, string.Empty, 100, "done", 0, 0, "Nothing to move.");
                    return;
                }

                // Same-volume moves are instant renames; only cross-drive files need room at the target.
                long requiredBytes = outside.Where(o => o.CrossDrive).Sum(o => o.SizeBytes);
                long? freeBytes = StorageService.GetContentDriveFreeBytes();

                if (freeBytes.HasValue && requiredBytes > 0 && requiredBytes + SafetyMarginBytes > freeBytes.Value)
                {
                    double needGb = (double)(requiredBytes + SafetyMarginBytes) / StorageService.BYTES_PER_GB;
                    double freeGb = (double)freeBytes.Value / StorageService.BYTES_PER_GB;
                    Log.Warning("Content migration aborted: need {Need:F2} GB free, only {Free:F2} GB available.", needGb, freeGb);

                    await MessageService.ShowModal(
                        "Not enough space",
                        $"Moving these videos needs about {needGb:F1} GB free on the recording drive, but only {freeGb:F1} GB is available.\n\nFree up some space or pick a different recording path, then try again.",
                        "error");

                    await SendProgress(migrationId, string.Empty, 0, "error", 0, outside.Count, "Not enough free space.");
                    return;
                }

                long totalBytes = Math.Max(1, outside.Sum(o => o.SizeBytes));
                long movedBytes = 0;
                int index = 0;
                int succeeded = 0;
                int failed = 0;

                foreach (var item in outside)
                {
                    index++;
                    string displayName = !string.IsNullOrWhiteSpace(item.Content.Title)
                        ? item.Content.Title
                        : item.Content.FileName;

                    await SendProgress(migrationId, displayName, (double)movedBytes / totalBytes * 100, "migrating", index, outside.Count);

                    try
                    {
                        if (await MigrateOne(item, contentFolder, totalBytes, movedBytes, migrationId, displayName, index, outside.Count))
                        {
                            succeeded++;
                            movedBytes += item.SizeBytes;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Log.Error(ex, "Failed to migrate content {File}", item.Content.FilePath);
                    }
                }

                await SettingsService.LoadContentFromFolderIntoState(sendToFrontend: true);

                string status = failed == 0 ? "done" : "error";
                string summary = failed == 0
                    ? $"Moved {succeeded} video{(succeeded == 1 ? string.Empty : "s")} to your recording path."
                    : $"Moved {succeeded} video{(succeeded == 1 ? string.Empty : "s")}, {failed} could not be moved. See logs for details.";

                await SendProgress(migrationId, string.Empty, 100, status, outside.Count, outside.Count, summary);
                Log.Information("Content migration complete: {Succeeded} moved, {Failed} failed.", succeeded, failed);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected error during content migration.");
                await SendProgress(migrationId, string.Empty, 0, "error", 0, 0, "Migration failed unexpectedly.");
            }
            finally
            {
                lock (_lock)
                {
                    _isMigrating = false;
                }
            }
        }

        private static async Task<bool> MigrateOne(OutsideContent item, string contentFolder, long totalBytes, long movedBytesBefore, string migrationId, string displayName, int index, int total)
        {
            Content content = item.Content;
            string sourcePath = content.FilePath;

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                Log.Warning("Skipping migration; source file is missing: {File}", sourcePath);
                return false;
            }

            var recording = AppState.Instance.Recording;
            if (recording != null && PathsEqual(recording.FilePath, sourcePath))
            {
                Log.Information("Skipping migration of the active recording file: {File}", sourcePath);
                return false;
            }

            string gameFolder = StorageService.SanitizeGameNameForFolder(content.Game);
            string destFolder = PathUtils.Combine(FolderNames.GetVideoFolderPath(contentFolder, content.Type), gameFolder);
            string destPath = PathUtils.Combine(destFolder, Path.GetFileName(sourcePath));

            if (PathsEqual(destPath, sourcePath))
                return false;

            Directory.CreateDirectory(destFolder);

            if (File.Exists(destPath))
            {
                Log.Warning("Skipping migration; a file already exists at the destination: {Dest}", destPath);
                return false;
            }

            if (DrivesEqual(sourcePath, destPath))
            {
                File.Move(sourcePath, destPath);

                // Roll the rename back if the sidecar can't be updated, otherwise the file is orphaned.
                if (!await TryUpdateSidecarPath(content, destPath))
                {
                    TryMoveBack(destPath, sourcePath);
                    throw new IOException($"Could not update metadata for {sourcePath}; rolled the move back.");
                }
            }
            else
            {
                // Cross-volume: copy to temp, verify, swap in, and only delete the source after the
                // sidecar is committed so an interrupted run never loses or orphans the original.
                string tempPath = destPath + ".segramigrating";
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);

                    await CopyWithProgressAsync(sourcePath, tempPath, movedBytesBefore, totalBytes, migrationId, displayName, index, total);

                    long sourceLen = new FileInfo(sourcePath).Length;
                    long tempLen = new FileInfo(tempPath).Length;
                    if (sourceLen != tempLen)
                        throw new IOException($"Copy size mismatch ({tempLen} != {sourceLen}) for {sourcePath}");

                    File.Move(tempPath, destPath);
                }
                catch
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
                    throw;
                }

                if (!await TryUpdateSidecarPath(content, destPath))
                {
                    try { if (File.Exists(destPath)) File.Delete(destPath); } catch { /* best effort */ }
                    throw new IOException($"Could not update metadata for {sourcePath}; kept the original and removed the copy.");
                }

                await TryDeleteSourceAsync(sourcePath);
            }

            TryRemoveEmptyDirectory(Path.GetDirectoryName(sourcePath));

            Log.Information("Migrated content {Old} -> {New}", sourcePath, destPath);
            return true;
        }

        private static async Task CopyWithProgressAsync(string source, string dest, long movedBytesBefore, long totalBytes, string migrationId, string displayName, int index, int total)
        {
            const int bufferSize = 1024 * 1024;
            byte[] buffer = new byte[bufferSize];
            long fileCopied = 0;
            long lastReportedPercent = -1;

            using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

            int read;
            while ((read = await src.ReadAsync(buffer.AsMemory(0, bufferSize))) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                fileCopied += read;

                double overall = (double)(movedBytesBefore + fileCopied) / totalBytes * 100;
                long rounded = (long)overall;
                if (rounded != lastReportedPercent)
                {
                    lastReportedPercent = rounded;
                    await SendProgress(migrationId, displayName, overall, "migrating", index, total);
                }
            }

            await dst.FlushAsync();
        }

        private static Task SendProgress(string id, string fileName, double progress, string status, int currentFileIndex, int totalFiles, string? message = null)
        {
            return MessageService.SendFrontendMessage(ProgressMessage, new
            {
                id,
                fileName,
                progress = Math.Clamp(progress, 0, 100),
                status,
                totalFiles,
                currentFileIndex,
                message
            });
        }

        // Returns false when the update fails (UpdateMetadataFile swallows errors and returns null).
        private static async Task<bool> TryUpdateSidecarPath(Content content, string newFilePath)
        {
            string sidecar = PathUtils.Combine(FolderNames.GetMetadataFolderPath(content.Type), content.FileName + ".json");
            var updated = await ContentService.UpdateMetadataFile(sidecar, c => c.FilePath = newFilePath);
            return updated != null;
        }

        private static void TryMoveBack(string from, string to)
        {
            try
            {
                if (File.Exists(from) && !File.Exists(to))
                    File.Move(from, to);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to roll migration back ({From} -> {To}): {Message}. The video may need manual recovery.", from, to, ex.Message);
            }
        }

        private static async Task TryDeleteSourceAsync(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    Log.Warning("Left a duplicate at {Path} after migration; could not delete source: {Message}", path, ex.Message);
                    return;
                }
            }

            Log.Warning("Left a duplicate at {Path} after migration; source remained locked.", path);
        }

        private static string? DriveRoot(string path)
        {
            try
            {
                return Path.GetPathRoot(Path.GetFullPath(path));
            }
            catch
            {
                return null;
            }
        }

        private static bool DrivesEqual(string a, string b)
        {
            return string.Equals(DriveRoot(a), DriveRoot(b), StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void TryRemoveEmptyDirectory(string? dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    Log.Information("Removed empty source folder: {Dir}", dir);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Could not remove empty source folder {Dir}: {Message}", dir, ex.Message);
            }
        }
    }
}
