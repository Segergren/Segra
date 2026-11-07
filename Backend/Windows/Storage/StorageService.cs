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

        public static string GetOutputFolder(string typeFolder, string? gameName) =>
            Settings.Instance.GameSubfolders
                ? PathUtils.Combine(typeFolder, SanitizeGameNameForFolder(gameName ?? "Unknown"))
                : typeFolder;

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
        public sealed record FullDrive(string Label, string Root, double FreeGb);

        // System and temp drives are considered full below this much free space
        public const long MinimumSystemFreeSpaceBytes = 2L * 1024 * 1024 * 1024; // 2 GB

        // Absolute floor for the while-recording low-space stop. The effective threshold scales up
        // with the configured bitrate (see OBSService), but never drops below this so OBS can always
        // finalize the file cleanly instead of slamming into a completely full disk.
        public const long MinimumRecordingFreeSpaceBytes = 250L * 1024 * 1024; // 250 MB

        internal static string? GetDriveRoot(string path) =>
            OperatingSystem.IsWindows() ? Path.GetPathRoot(path) : path;

        // Returns the free space (in bytes) on the drive holding the content folder,
        // or null if it cannot be determined (so callers can choose not to act on errors).
        public static long? GetContentDriveFreeBytes()
        {
            try
            {
                string contentFolder = Settings.Instance.ContentFolder;
                if (string.IsNullOrEmpty(contentFolder)) return null;

                string? root = GetDriveRoot(contentFolder);
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

                string? root = GetDriveRoot(contentFolder);
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

        // Checks the system (C:) and temp drives; the recording drive has its own bitrate-based check.
        // Returns any drive below MinimumSystemFreeSpaceBytes free, deduplicated by drive root.
        public static List<FullDrive> GetFullDrives()
        {
            var stopwatch = Stopwatch.StartNew();
            var checks = new (string Label, string Path)[]
            {
                ("System drive", Environment.SystemDirectory),
                ("Temp drive", Path.GetTempPath())
            };

            var fullDrives = new List<FullDrive>();
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (label, path) in checks)
            {
                try
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    string? root = GetDriveRoot(path);
                    if (string.IsNullOrEmpty(root)) continue;
                    if (!seenRoots.Add(root)) continue; // same physical drive already checked

                    var drive = new DriveInfo(root);
                    if (!drive.IsReady || drive.TotalSize <= 0) continue;

                    if (drive.AvailableFreeSpace < MinimumSystemFreeSpaceBytes)
                    {
                        fullDrives.Add(new FullDrive(label, root, drive.AvailableFreeSpace / (double)BYTES_PER_GB));
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
            if (Settings.Instance.UnlimitedStorage)
                return;

            Log.Information("Starting storage limit check");
            long storageLimit = Settings.Instance.StorageLimit; // This is in GB
            string contentFolder = Settings.Instance.ContentFolder;

            if (string.IsNullOrEmpty(contentFolder) || !Directory.Exists(contentFolder))
            {
                Log.Information("Content folder does not exist, skipping storage limit check");
                return;
            }

            long currentUsageBytes = CalculateLimitedSize(contentFolder);
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

        // The size the storage limit is measured against
        internal static long CalculateLimitedSize(string contentFolder)
        {
            long size = CalculateFolderSize(contentFolder);
            if (Settings.Instance.ExcludeClipsFromStorageLimit)
            {
                foreach (string name in new[] { FolderNames.Clips, FolderNames.Highlights })
                {
                    string folder = Path.Combine(contentFolder, name);
                    if (Directory.Exists(folder))
                        size -= CalculateFolderSize(folder);
                }
            }
            return size;
        }

        internal static long CalculateFolderSize(string folderPath)
        {
            long size = 0;
            string[] files = Directory.GetFiles(folderPath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 });

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

        /// <summary>
        /// Deletes the oldest sessions and replay buffers when the drive is low on space during a recording,
        /// so the recording can continue. Returns false when it could not reach the threshold.
        /// </summary>
        public static async Task<bool> TryFreeSpaceForRecording(long thresholdBytes, long targetFreeBytes, string? activeFilePath)
        {
            if (Settings.Instance.UnlimitedStorage)
                return false;

            string contentFolder = Settings.Instance.ContentFolder;
            long? freeBytes = GetContentDriveFreeBytes();
            if (freeBytes == null || string.IsNullOrEmpty(contentFolder) || !Directory.Exists(contentFolder))
                return false;

            long deletableBytes = GetDeletionCandidates(contentFolder, activeFilePath).Sum(f => f.Length);
            if (freeBytes.Value + deletableBytes < thresholdBytes)
            {
                Log.Information("Not enough old recordings to free space for the current recording");
                return false;
            }

            Log.Information($"Drive low on space ({freeBytes.Value / (1024 * 1024)} MB free), deleting old recordings to keep recording");
            long freed = await DeleteOldestContent(contentFolder, targetFreeBytes - freeBytes.Value, activeFilePath);
            return freed > 0 && GetContentDriveFreeBytes() >= thresholdBytes;
        }

        // Do not delete files newer than 1 hour since they are likely still in use
        private static List<FileInfo> GetDeletionCandidates(string contentFolder, string? excludeFilePath)
        {
            DateTime oneHourAgo = DateTime.Now.AddHours(-1);
            string? excluded = excludeFilePath == null ? null : PathUtils.Normalize(excludeFilePath);
            List<FileInfo> deletionCandidates = new List<FileInfo>();

            foreach (string folderName in new[] { FolderNames.Sessions, FolderNames.Buffers })
            {
                string folder = Path.Combine(contentFolder, folderName);
                if (!Directory.Exists(folder))
                    continue;

                // Search recursively to find files in game subfolders
                deletionCandidates.AddRange(Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .Where(f => f.LastWriteTime < oneHourAgo
                        && (excluded == null || !string.Equals(PathUtils.Normalize(f.FullName), excluded, StringComparison.OrdinalIgnoreCase))));
            }

            return deletionCandidates.OrderBy(f => f.CreationTime).ToList();
        }

        private static async Task<long> DeleteOldestContent(string contentFolder, long spaceToFreeBytes, string? excludeFilePath = null)
        {
            double spaceToFreeGB = (double)spaceToFreeBytes / BYTES_PER_GB;
            List<FileInfo> deletionCandidates = GetDeletionCandidates(contentFolder, excludeFilePath);
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

                    var content = AppState.Instance.Content.FirstOrDefault(c =>
                        c.Type == contentType &&
                        string.Equals(PathUtils.Normalize(c.FilePath), fileFullName, StringComparison.OrdinalIgnoreCase));
                    string? contentId = content?.Id;

                    // Deleting the video also deletes its webcam file
                    string? webcamPath = content?.Webcam?.FilePath;
                    if (!string.IsNullOrEmpty(webcamPath) && File.Exists(webcamPath))
                        fileSize += new FileInfo(webcamPath).Length;

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

            return freedSpaceBytes;
        }
    }
}
