using Segra.Backend.Core.Models;
using Segra.Backend.Media;
using Serilog;

namespace Segra.Backend.Windows.Storage
{
    internal class StorageService
    {
        private const long BYTES_PER_GB = 1073741824; // 1024 * 1024 * 1024

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

            string sessionsFolder = Path.Combine(contentFolder, "sessions");
            string buffersFolder = Path.Combine(contentFolder, "buffers");

            if (Directory.Exists(sessionsFolder))
            {
                var sessionFiles = Directory.GetFiles(sessionsFolder, "*", SearchOption.TopDirectoryOnly)
                    .Select(f => new FileInfo(f))
                    .Where(f => f.LastWriteTime < oneHourAgo);

                deletionCandidates.AddRange(sessionFiles);
                Log.Information($"Found {sessionFiles.Count()} eligible session files older than 1 hour");
            }

            if (Directory.Exists(buffersFolder))
            {
                var bufferFiles = Directory.GetFiles(buffersFolder, "*", SearchOption.TopDirectoryOnly)
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
                    // Determine content type based on parent folder

                    if (file.Directory == null)
                    {
                        Log.Error("File directory is null");
                        continue;
                    }
                    string parentFolder = file.Directory.Name.ToLower();
                    Content.ContentType contentType = parentFolder == "sessions" ?
                        Content.ContentType.Session : Content.ContentType.Buffer;

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
