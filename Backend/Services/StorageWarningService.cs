using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Windows.Storage;
using Serilog;
using System.Text.Json;

namespace Segra.Backend.Services
{
    /// <summary>
    /// Service for handling storage-related warnings and user confirmations
    /// </summary>
    public static class StorageWarningService
    {
        // Store pending imports waiting for user confirmation
        private static readonly Dictionary<string, PendingImport> _pendingImports = new();

        // Store pending content folder changes waiting for user confirmation
        private static readonly Dictionary<string, string> _pendingContentFolderChanges = new();

        private class PendingImport
        {
            public required string[] Files { get; set; }
            public required Content.ContentType ContentType { get; set; }
        }

        /// <summary>
        /// Handles the storage warning confirmation from the frontend
        /// </summary>
        public static async Task HandleStorageWarningConfirm(JsonElement parameters)
        {
            try
            {
                if (!parameters.TryGetProperty("warningId", out JsonElement warningIdElement) ||
                    !parameters.TryGetProperty("confirmed", out JsonElement confirmedElement) ||
                    !parameters.TryGetProperty("action", out JsonElement actionElement))
                {
                    Log.Error("Missing required parameters in StorageWarningConfirm");
                    return;
                }

                string warningId = warningIdElement.GetString()!;
                bool confirmed = confirmedElement.GetBoolean();
                string action = actionElement.GetString()!;

                if (action == "import")
                {
                    if (!_pendingImports.TryGetValue(warningId, out PendingImport? pendingImport))
                    {
                        Log.Warning($"No pending import found for warningId: {warningId}");
                        return;
                    }

                    // Remove from pending imports
                    _pendingImports.Remove(warningId);

                    if (confirmed)
                    {
                        Log.Information($"User confirmed import despite storage warning");
                        _ = Task.Run(() => Media.ImportService.ExecuteImport(pendingImport.Files, pendingImport.ContentType));
                    }
                    else
                    {
                        Log.Information($"User cancelled import due to storage warning");
                    }
                }
                else if (action == "contentFolder")
                {
                    if (!_pendingContentFolderChanges.TryGetValue(warningId, out string? newContentFolder))
                    {
                        Log.Warning($"No pending content folder change found for warningId: {warningId}");
                        return;
                    }

                    // Remove from pending changes
                    _pendingContentFolderChanges.Remove(warningId);

                    if (confirmed)
                    {
                        Log.Information($"User confirmed content folder change despite storage warning");
                        // Apply the content folder change
                        Settings.Instance.ContentFolder = newContentFolder;
                    }
                    else
                    {
                        Log.Information($"User cancelled content folder change due to storage warning");
                        // Send settings back to frontend to reset the UI
                        await MessageService.SendSettingsToFrontend("Content folder change cancelled");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error handling storage warning confirmation: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if changing to a new content folder would exceed the storage limit.
        /// Returns true if the change should proceed, false if a warning was sent.
        /// </summary>
        public static async Task<bool> CheckContentFolderChange(string newContentFolder)
        {
            try
            {
                // Calculate the size of the new folder
                if (!Directory.Exists(newContentFolder))
                {
                    // New folder doesn't exist yet, no content to check
                    return true;
                }

                long newFolderSizeBytes = CalculateFolderSize(newContentFolder);
                double newFolderSizeGb = (double)newFolderSizeBytes / StorageService.BYTES_PER_GB;
                int storageLimitGb = Settings.Instance.StorageLimit;

                if (newFolderSizeGb > storageLimitGb)
                {
                    // Store pending change and send warning to frontend
                    string warningId = Guid.NewGuid().ToString();
                    _pendingContentFolderChanges[warningId] = newContentFolder;

                    await MessageService.SendFrontendMessage("StorageWarning", new
                    {
                        warningId,
                        title = "Storage Limit Warning",
                        description = $"The selected folder contains {newFolderSizeGb:F2} GB of content, which exceeds your storage limit of {storageLimitGb} GB.\n\n" +
                                     $"Older recordings may be automatically deleted to stay within the limit.\n\n" +
                                     $"Do you want to use this folder anyway?",
                        confirmText = "Use Folder",
                        cancelText = "Cancel",
                        action = "contentFolder",
                        actionData = new { warningId }
                    });

                    Log.Information($"Storage warning sent for content folder change. Folder size: {newFolderSizeGb:F2} GB, Limit: {storageLimitGb} GB");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Error checking content folder size: {ex.Message}");
                return true; // Allow the change on error
            }
        }

        /// <summary>
        /// Checks if importing files would exceed the storage limit.
        /// Returns true if the import should proceed, false if a warning was sent.
        /// </summary>
        public static async Task<bool> CheckImportStorageLimit(string[] selectedFiles, Content.ContentType contentType)
        {
            try
            {
                long totalImportSizeBytes = 0;
                foreach (string file in selectedFiles)
                {
                    if (File.Exists(file))
                    {
                        totalImportSizeBytes += new FileInfo(file).Length;
                    }
                }

                double totalImportSizeGb = (double)totalImportSizeBytes / StorageService.BYTES_PER_GB;
                double currentFolderSizeGb = StorageService.GetCurrentFolderSizeGb();
                double projectedSizeGb = currentFolderSizeGb + totalImportSizeGb;
                int storageLimitGb = Settings.Instance.StorageLimit;

                if (projectedSizeGb > storageLimitGb)
                {
                    // Store pending import and send warning to frontend
                    string warningId = Guid.NewGuid().ToString();
                    _pendingImports[warningId] = new PendingImport
                    {
                        Files = selectedFiles,
                        ContentType = contentType
                    };

                    await MessageService.SendFrontendMessage("StorageWarning", new
                    {
                        warningId,
                        title = "Storage Limit Warning",
                        description = $"Importing these files ({totalImportSizeGb:F2} GB) will exceed your storage limit.\n\n" +
                                     $"Current folder size: {currentFolderSizeGb:F2} GB\n" +
                                     $"Files to import: {totalImportSizeGb:F2} GB\n" +
                                     $"Projected total: {projectedSizeGb:F2} GB\n" +
                                     $"Storage limit: {storageLimitGb} GB\n\n" +
                                     $"Older recordings may be automatically deleted to make room.\n\n" +
                                     $"Do you want to continue with the import?",
                        confirmText = "Import Anyway",
                        cancelText = "Cancel",
                        action = "import",
                        actionData = new { warningId }
                    });

                    Log.Information($"Storage warning sent for import. Projected: {projectedSizeGb:F2} GB, Limit: {storageLimitGb} GB");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Error checking import storage limit: {ex.Message}");
                return true; // Allow the import on error
            }
        }

        private static long CalculateFolderSize(string folderPath)
        {
            long size = 0;
            try
            {
                string[] files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
                foreach (string file in files)
                {
                    try
                    {
                        FileInfo fileInfo = new FileInfo(file);
                        size += fileInfo.Length;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Error calculating size for file {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Error enumerating files in folder {folderPath}: {ex.Message}");
            }
            return size;
        }
    }
}
