using Serilog;
using System.Diagnostics;
using Segra.Backend.Media;
using Segra.Backend.Shared;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Windows.Storage
{
    internal class StorageService
    {
        public const long BYTES_PER_GB = 1024L * 1024 * 1024;

        public static string SanitizeGameNameForFolder(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                return "Unknown";

            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = new string(gameName
                .Select(c => invalidChars.Contains(c) ? '_' : c)
                .ToArray());

            sanitized = sanitized.Trim().Trim('.');

            while (sanitized.Contains("__"))
                sanitized = sanitized.Replace("__", "_");

            return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
        }

        public static double GetCurrentFolderSizeGb()
        {
            string contentFolder = Settings.Instance.ContentFolder;
            if (string.IsNullOrEmpty(contentFolder) || !Directory.Exists(contentFolder))
            {
                return 0;
            }

            long currentUsageBytes = CalculateFolderSize(contentFolder);
            double currentUsageGb = Math.Round((double)currentUsageBytes / BYTES_PER_GB, 2);
            return currentUsageGb;
        }

        // A drive considered too full to safely start a recording
        public sealed record FullDrive(string Label, string Root, double UsedPercent);

        // Drives are considered full at or above this used percentage
        public const double DriveFullThresholdPercent = 99.0;

        // Absolute floor for the while-recording low-space stop. The effective threshold scales up
        // with the configured bitrate (see OBSService), but never drops below this so OBS can always
        // finalize the file cleanly instead of slamming into a completely full disk.
        public const long MinimumRecordingFreeSpaceBytes = 250L * 1024 * 1024; // 250 MB
        public const long MinimumOperationalFreeSpaceBytes = 100L * 1024 * 1024; // 100 MB

        public enum StorageLocationProblem
        {
            None,
            InvalidPath,
            Unavailable,
            NotWritable,
            InsufficientSpace
        }

        public sealed record StorageLocationCheck(
            string Label,
            string Path,
            string? Root,
            bool IsAvailable,
            StorageLocationProblem Problem,
            string Description,
            long? AvailableFreeBytes = null);

        public static StorageLocationCheck CheckStorageLocation(
            string label,
            string path,
            long minimumFreeBytes = MinimumOperationalFreeSpaceBytes)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Failure(StorageLocationProblem.InvalidPath, "No folder is configured.");
            }

            string fullPath;
            string? root;
            try
            {
                fullPath = Path.GetFullPath(path);
                root = Path.GetPathRoot(fullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Failure(StorageLocationProblem.InvalidPath, $"The configured path is invalid: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return new StorageLocationCheck(
                    label,
                    fullPath,
                    root,
                    false,
                    StorageLocationProblem.Unavailable,
                    "The drive or network share is not currently available.");
            }

            long? availableFreeBytes = TryGetAvailableFreeSpace(root);
            if (availableFreeBytes.HasValue && availableFreeBytes.Value < minimumFreeBytes)
            {
                double freeMb = availableFreeBytes.Value / (1024.0 * 1024.0);
                return new StorageLocationCheck(
                    label,
                    fullPath,
                    root,
                    false,
                    StorageLocationProblem.InsufficientSpace,
                    $"The drive only has {freeMb:F0} MB free.",
                    availableFreeBytes);
            }

            string? probePath = null;
            try
            {
                Directory.CreateDirectory(fullPath);
                probePath = Path.Combine(fullPath, $".segra-write-test-{Guid.NewGuid():N}.tmp");
                using (var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None))
                {
                    probe.WriteByte(0);
                    probe.Flush();
                }

                File.Delete(probePath);
                probePath = null;

                return new StorageLocationCheck(
                    label,
                    fullPath,
                    root,
                    true,
                    StorageLocationProblem.None,
                    string.Empty,
                    availableFreeBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                StorageLocationProblem problem = IsDiskFull(ex)
                    ? StorageLocationProblem.InsufficientSpace
                    : ex is UnauthorizedAccessException
                        ? StorageLocationProblem.NotWritable
                        : StorageLocationProblem.Unavailable;

                string description = problem switch
                {
                    StorageLocationProblem.InsufficientSpace => "The drive is full or does not have enough free space.",
                    StorageLocationProblem.NotWritable => "Segra does not have permission to write to this folder.",
                    _ => "The drive or network share became unavailable."
                };

                Log.Warning(ex, "Storage location check failed for {Label} at {Path}", label, fullPath);
                return new StorageLocationCheck(label, fullPath, root, false, problem, description, availableFreeBytes);
            }
            finally
            {
                if (probePath != null)
                {
                    try { File.Delete(probePath); }
                    catch { /* best-effort cleanup of the write probe */ }
                }
            }

            StorageLocationCheck Failure(StorageLocationProblem problem, string description) =>
                new(label, path, null, false, problem, description);
        }

        public static bool IsDiskFull(Exception exception)
        {
            int nativeError = exception.HResult & 0xFFFF;
            return nativeError is 39 or 112 ||
                exception.Message.Contains("no space left", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase);
        }

        private static long? TryGetAvailableFreeSpace(string root)
        {
            try
            {
                var drive = new DriveInfo(root);
                return drive.IsReady ? drive.AvailableFreeSpace : null;
            }
            catch
            {
                // Some network providers allow file access but do not expose capacity through DriveInfo.
                return null;
            }
        }

        // Returns the free space (in bytes) on the drive holding the content folder,
        // or null if it cannot be determined (so callers can choose not to act on errors).
        public static long? GetContentDriveFreeBytes()
        {
            try
            {
                string contentFolder = Settings.Instance.ContentFolder;
                if (string.IsNullOrEmpty(contentFolder)) return null;

                string? root = Path.GetPathRoot(contentFolder);
                if (string.IsNullOrEmpty(root)) return null;

                var drive = new DriveInfo(root);
                if (!drive.IsReady) return null;

                return drive.AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                Log.Warning($"Error checking content drive free space: {ex.Message}");
                return null;
            }
        }

        public static (double UsedGb, double FreeGb)? GetContentDriveSpaceGb()
        {
            try
            {
                string contentFolder = Settings.Instance.ContentFolder;
                if (string.IsNullOrEmpty(contentFolder)) return null;

                string? root = Path.GetPathRoot(contentFolder);
                if (string.IsNullOrEmpty(root)) return null;

                var drive = new DriveInfo(root);
                if (!drive.IsReady || drive.TotalSize <= 0) return null;

                double freeGb = drive.AvailableFreeSpace / (double)BYTES_PER_GB;
                double usedGb = (drive.TotalSize - drive.AvailableFreeSpace) / (double)BYTES_PER_GB;
                return (Math.Round(usedGb, 2), Math.Round(freeGb, 2));
            }
            catch (Exception ex)
            {
                Log.Warning($"Error reading content drive space: {ex.Message}");
                return null;
            }
        }

        // Checks the system (C:), recording (content folder) and temp drives.
        // Returns any drive that is at or above DriveFullThresholdPercent, deduplicated by drive root.
        public static List<FullDrive> GetFullDrives()
        {
            var stopwatch = Stopwatch.StartNew();
            var checks = new (string Label, string Path)[]
            {
                ("System drive", Environment.SystemDirectory),
                ("Recording drive", Settings.Instance.ContentFolder),
                ("Temp drive", Path.GetTempPath())
            };

            var fullDrives = new List<FullDrive>();
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (label, path) in checks)
            {
                try
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    string? root = Path.GetPathRoot(path);
                    if (string.IsNullOrEmpty(root)) continue;
                    if (!seenRoots.Add(root)) continue; // same physical drive already checked

                    var drive = new DriveInfo(root);
                    if (!drive.IsReady || drive.TotalSize <= 0) continue;

                    double usedPercent = (drive.TotalSize - drive.AvailableFreeSpace) / (double)drive.TotalSize * 100.0;
                    if (usedPercent >= DriveFullThresholdPercent)
                    {
                        fullDrives.Add(new FullDrive(label, root, usedPercent));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error checking drive space for {path}: {ex.Message}");
                }
            }

            stopwatch.Stop();
            Log.Information($"Drive space check took {stopwatch.Elapsed.TotalMilliseconds:F2} ms ({fullDrives.Count} full)");
            return fullDrives;
        }

        public static void UpdateFolderSizeInState(bool sendToFrontend = true)
        {
            double currentSizeGb = GetCurrentFolderSizeGb();
            AppState.Instance.SetCurrentFolderSizeGb(currentSizeGb, sendToFrontend);
            UpdateRecordingDriveSpaceInState(sendToFrontend);
            Log.Information($"Updated folder size in state: {currentSizeGb:F2} GB");
        }

        public static void UpdateRecordingDriveSpaceInState(bool sendToFrontend = true)
        {
            var driveSpace = GetContentDriveSpaceGb();
            AppState.Instance.SetRecordingDriveSpaceGb(
                driveSpace?.UsedGb,
                driveSpace?.FreeGb,
                sendToFrontend
            );

            if (driveSpace != null)
            {
                Log.Information(
                    $"Updated recording drive space in state: {driveSpace.Value.UsedGb:F2} GB used, {driveSpace.Value.FreeGb:F2} GB free"
                );
            }
            else
            {
                Log.Warning("Recording drive space is unavailable");
            }
        }

        public static async Task EnsureStorageBelowLimit()
        {
            Log.Information("Starting storage limit check");
            long storageLimit = Settings.Instance.StorageLimit; // This is in GB
            string contentFolder = Settings.Instance.ContentFolder;

            if (string.IsNullOrEmpty(contentFolder) || !Directory.Exists(contentFolder))
            {
                Log.Information("Content folder does not exist, skipping storage limit check");
                return;
            }

            long currentUsageBytes = CalculateFolderSize(contentFolder);
            double currentUsageGB = (double)currentUsageBytes / BYTES_PER_GB;

            Log.Information($"Current storage usage: {currentUsageGB:F2} GB, limit: {storageLimit} GB");

            if (currentUsageBytes > storageLimit * BYTES_PER_GB)
            {
                double excessGB = (currentUsageBytes - (storageLimit * BYTES_PER_GB)) / (double)BYTES_PER_GB;
                Log.Information($"Storage limit exceeded by {excessGB:F2} GB, starting cleanup");
                await DeleteOldestContent(contentFolder, currentUsageBytes - (storageLimit * BYTES_PER_GB));
            }
            else
            {
                Log.Information("Storage usage is within limits, no cleanup needed");
            }
        }

        internal static long CalculateFolderSize(string folderPath)
        {
            long size = 0;
            string[] files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);

            foreach (string file in files)
            {
                FileInfo fileInfo = new FileInfo(file);
                try
                {
                    size += fileInfo.Length;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error calculating size for file {PathUtils.Normalize(file)}: {ex.Message}");
                }
            }

            return size;
        }

        private static async Task DeleteOldestContent(string contentFolder, long spaceToFreeBytes)
        {
            double spaceToFreeGB = (double)spaceToFreeBytes / BYTES_PER_GB;

            // Do not delete files older than 1 hour since they are likely still in use
            DateTime oneHourAgo = DateTime.Now.AddHours(-1);
            List<FileInfo> deletionCandidates = new List<FileInfo>();

            string sessionsFolder = Path.Combine(contentFolder, FolderNames.Sessions);
            string buffersFolder = Path.Combine(contentFolder, FolderNames.Buffers);

            if (Directory.Exists(sessionsFolder))
            {
                // Search recursively to find files in game subfolders
                var sessionFiles = Directory.GetFiles(sessionsFolder, "*", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .Where(f => f.LastWriteTime < oneHourAgo);

                deletionCandidates.AddRange(sessionFiles);
                Log.Information($"Found {sessionFiles.Count()} eligible session files older than 1 hour");
            }

            if (Directory.Exists(buffersFolder))
            {
                // Search recursively to find files in game subfolders
                var bufferFiles = Directory.GetFiles(buffersFolder, "*", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .Where(f => f.LastWriteTime < oneHourAgo);

                deletionCandidates.AddRange(bufferFiles);
                Log.Information($"Found {bufferFiles.Count()} eligible buffer files older than 1 hour");
            }

            deletionCandidates = deletionCandidates.OrderBy(f => f.CreationTime).ToList();
            Log.Information($"Total files eligible for deletion: {deletionCandidates.Count}, ordered by creation time");

            long freedSpaceBytes = 0;
            int deletedCount = 0;

            foreach (FileInfo file in deletionCandidates)
            {
                if (freedSpaceBytes >= spaceToFreeBytes)
                    break;

                string fileFullName = PathUtils.Normalize(file.FullName);
                long fileSize = file.Length;
                double fileSizeMB = (double)fileSize / (1024 * 1024);

                try
                {
                    // Determine content type based on path (handles game subfolders)
                    Content.ContentType? detectedType = FolderNames.GetContentTypeFromPath(fileFullName);
                    if (detectedType == null)
                    {
                        Log.Warning($"Could not determine content type for file: {fileFullName}");
                        continue;
                    }
                    Content.ContentType contentType = detectedType.Value;

                    string? contentId = AppState.Instance.Content.FirstOrDefault(c =>
                        c.Type == contentType &&
                        string.Equals(PathUtils.Normalize(c.FilePath), fileFullName, StringComparison.OrdinalIgnoreCase))?.Id;

                    Log.Information($"Deleting {contentType} file: {fileFullName} ({fileSizeMB:F2} MB)");
                    await ContentService.DeleteContent(fileFullName, contentType, contentId);

                    freedSpaceBytes += fileSize;
                    deletedCount++;

                    double freedSpaceGB = (double)freedSpaceBytes / BYTES_PER_GB;
                    Log.Information($"Successfully deleted file, freed space so far: {freedSpaceGB:F2} GB");
                }
                catch (Exception ex)
                {
                    Log.Error($"Error deleting file {fileFullName}: {ex.Message}");
                }
            }

            double totalFreedGB = (double)freedSpaceBytes / BYTES_PER_GB;
            Log.Information($"Storage cleanup completed: {deletedCount} files deleted, {totalFreedGB:F2} GB freed");

            if (freedSpaceBytes < spaceToFreeBytes)
            {
                double stillNeededGB = (double)(spaceToFreeBytes - freedSpaceBytes) / BYTES_PER_GB;
                Log.Information($"Warning: Could not free enough space. Still needed: {stillNeededGB:F2} GB");
            }
        }
    }
}
