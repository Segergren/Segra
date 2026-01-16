using Segra.Backend.Core.Models;
using Segra.Backend.Media;
using Segra.Backend.Shared;
using Serilog;

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

        public static void UpdateFolderSizeInState()
        {
            double currentSizeGb = GetCurrentFolderSizeGb();
            Settings.Instance.State.CurrentFolderSizeGb = currentSizeGb;
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
                    Log.Warning($"Error calculating size for file {file}: {ex.Message}");
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

                long fileSize = file.Length;
                double fileSizeMB = (double)fileSize / (1024 * 1024);

                try
                {
                    // Determine content type based on path (handles game subfolders)
                    Content.ContentType? detectedType = FolderNames.GetContentTypeFromPath(file.FullName);
                    if (detectedType == null)
                    {
                        Log.Warning($"Could not determine content type for file: {file.FullName}");
                        continue;
                    }
                    Content.ContentType contentType = detectedType.Value;

                    Log.Information($"Deleting {contentType} file: {file.FullName} ({fileSizeMB:F2} MB)");
                    await ContentService.DeleteContent(file.FullName, contentType);

                    freedSpaceBytes += fileSize;
                    deletedCount++;

                    double freedSpaceGB = (double)freedSpaceBytes / BYTES_PER_GB;
                    Log.Information($"Successfully deleted file, freed space so far: {freedSpaceGB:F2} GB");
                }
                catch (Exception ex)
                {
                    Log.Error($"Error deleting file {file.FullName}: {ex.Message}");
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
