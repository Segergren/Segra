using Segra.Backend.Core.Models;
using Segra.Backend.Media;
using Segra.Backend.Shared;
using Serilog;
using System.Diagnostics;

namespace Segra.Backend.Windows.Storage
{
    internal class StorageService
    {
        public const long BYTES_PER_GB = 1073741824; // 1024 * 1024 * 1024

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

        public static void UpdateFolderSizeInState()
        {
            double currentSizeGb = GetCurrentFolderSizeGb();
            AppState.Instance.CurrentFolderSizeGb = currentSizeGb;
            Log.Information($"Updated folder size in state: {currentSizeGb:F2} GB");
        }

        public static async Task EnsureStorageBelowLimit()
        {
            Log.Information("Starting storage limit check");
            long storageLimit = Settings.Instance.StorageLimit; // This is in GB
            string contentFolder = Settings.Instance.ContentFolder;

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

        private static long CalculateFolderSize(string folderPath)
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

                    Log.Information($"Deleting {contentType} file: {fileFullName} ({fileSizeMB:F2} MB)");
                    await ContentService.DeleteContent(fileFullName, contentType);

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
