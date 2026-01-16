using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Services;
using Segra.Backend.Shared;
using Segra.Backend.Utils;
using Segra.Backend.Windows.Storage;
using Serilog;
using System.Text.Json;

namespace Segra.Backend.Media
{
    public static class ImportService
    {
        public static async Task HandleImportFile(JsonElement parameters)
        {
            int importId = Guid.NewGuid().GetHashCode();

            try
            {
                // Extract sectionId and determine content type
                if (!parameters.TryGetProperty("sectionId", out JsonElement sectionIdElement))
                {
                    Log.Error("sectionId not found in ImportFile parameters");
                    await MessageService.ShowModal("Import Error", "Missing section ID parameter", "error");
                    return;
                }

                string sectionId = sectionIdElement.GetString()!;
                Content.ContentType contentType;

                switch (sectionId)
                {
                    case "sessions":
                        contentType = Content.ContentType.Session;
                        break;
                    case "replayBuffer":
                        contentType = Content.ContentType.Buffer;
                        break;
                    default:
                        Log.Error($"Invalid sectionId: {sectionId}");
                        await MessageService.ShowModal("Import Error", $"Invalid section ID: {sectionId}", "error");
                        return;
                }

                // Open file dialog on dedicated STA thread to avoid reentrancy issues with WebView2
                string[]? selectedFiles = null;

                var tcs = new TaskCompletionSource<string[]?>();

                var staThread = new Thread(() =>
                {
                    try
                    {
                        using var openFileDialog = new OpenFileDialog
                        {
                            Filter = "MP4 Video Files (*.mp4)|*.mp4",
                            Title = "Import MP4 Video Files",
                            CheckFileExists = true,
                            CheckPathExists = true,
                            Multiselect = true
                        };

                        if (openFileDialog.ShowDialog() == DialogResult.OK)
                        {
                            tcs.SetResult(openFileDialog.FileNames);
                        }
                        else
                        {
                            tcs.SetResult(null);
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });

                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();

                selectedFiles = await tcs.Task;

                if (selectedFiles == null || selectedFiles.Length == 0)
                {
                    Log.Information("Import cancelled by user or no files selected");
                    return;
                }

                Log.Information($"Starting import of {selectedFiles.Length} file(s) to {contentType}");

                // Check if import would exceed storage limit
                bool shouldProceed = await StorageWarningService.CheckImportStorageLimit(selectedFiles, contentType);
                if (!shouldProceed)
                {
                    // Warning was sent to frontend, waiting for user confirmation
                    return;
                }

                // Proceed with import
                await ExecuteImport(selectedFiles, contentType);
            }
            catch (Exception ex)
            {
                Log.Error($"Error during import process: {ex.Message}");

                // Send error progress update for the entire import process
                try
                {
                    await MessageService.SendFrontendMessage("ImportProgress", new
                    {
                        id = importId,
                        progress = 0,
                        fileName = "Import Failed",
                        totalFiles = 0,
                        currentFileIndex = 0,
                        status = "error",
                        message = $"Import process failed: {ex.Message}"
                    });
                }
                catch (Exception msgEx)
                {
                    Log.Warning($"Failed to send import error message: {msgEx.Message}");
                }

                await MessageService.ShowModal("Import Error", $"An error occurred during import: {ex.Message}", "error");
            }
        }

        /// <summary>
        /// Executes the actual import of files
        /// </summary>
        public static async Task ExecuteImport(string[] selectedFiles, Content.ContentType contentType)
        {
            int importId = Guid.NewGuid().GetHashCode();

            // Base content folder
            string contentFolder = Settings.Instance.ContentFolder;
            string baseTypeFolder = Path.Combine(contentFolder, FolderNames.GetVideoFolderName(contentType));

            int importedCount = 0;
            int failedCount = 0;

            for (int i = 0; i < selectedFiles.Length; i++)
            {
                string sourceFile = selectedFiles[i];
                string originalFileName = Path.GetFileNameWithoutExtension(sourceFile);
                string fileExtension = Path.GetExtension(sourceFile);

                // Validate file extension is MP4
                if (!fileExtension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    failedCount++;
                    Log.Error($"Skipping {originalFileName}: Only MP4 files are allowed");
                    continue;
                }

                try
                {
                    // Create target directory with game subfolder
                    string targetFolder = Path.Combine(baseTypeFolder, "Unknown");
                    Directory.CreateDirectory(targetFolder);

                    // Send initial progress for current file
                    double progressPercent = (double)i / selectedFiles.Length * 100;
                    try
                    {
                        await MessageService.SendFrontendMessage("ImportProgress", new
                        {
                            id = importId,
                            progress = progressPercent,
                            fileName = originalFileName,
                            totalFiles = selectedFiles.Length,
                            currentFileIndex = i + 1,
                            status = "importing"
                        });
                    }
                    catch (Exception msgEx)
                    {
                        Log.Warning($"Failed to send import progress message: {msgEx.Message}");
                    }

                    // Generate unique filename with timestamp
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string targetFileName = $"{timestamp}_{originalFileName}{fileExtension}";
                    string targetFilePath = Path.Combine(targetFolder, targetFileName);

                    // Ensure unique filename if file already exists
                    int counter = 1;
                    while (File.Exists(targetFilePath))
                    {
                        targetFileName = $"{timestamp}_{originalFileName}_{counter}{fileExtension}";
                        targetFilePath = Path.Combine(targetFolder, targetFileName);
                        counter++;
                    }

                    Log.Information($"Importing {originalFileName} to {targetFilePath}");

                    // Copy the video file to target location
                    File.Copy(sourceFile, targetFilePath);

                    // Extract recording date from file
                    DateTime recordingDate = File.GetCreationTime(sourceFile);

                    // Send progress after file copy
                    try
                    {
                        await MessageService.SendFrontendMessage("ImportProgress", new
                        {
                            id = importId,
                            progress = progressPercent + (25.0 / selectedFiles.Length),
                            fileName = originalFileName,
                            totalFiles = selectedFiles.Length,
                            currentFileIndex = i + 1,
                            status = "importing"
                        });
                    }
                    catch (Exception msgEx)
                    {
                        Log.Warning($"Failed to send import progress message: {msgEx.Message}");
                    }

                    // Create metadata file with detected game name and date
                    await ContentService.CreateMetadataFile(targetFilePath, contentType, "Unknown", null, originalFileName.Replace("_", " "), recordingDate != DateTime.MinValue ? recordingDate : null, isImported: true);

                    // Ensure file is fully written to disk/network before thumbnail generation
                    await GeneralUtils.EnsureFileReady(targetFilePath);

                    // Send progress after metadata creation
                    try
                    {
                        await MessageService.SendFrontendMessage("ImportProgress", new
                        {
                            id = importId,
                            progress = progressPercent + (50.0 / selectedFiles.Length),
                            fileName = originalFileName,
                            totalFiles = selectedFiles.Length,
                            currentFileIndex = i + 1,
                            status = "importing"
                        });
                    }
                    catch (Exception msgEx)
                    {
                        Log.Warning($"Failed to send import progress message: {msgEx.Message}");
                    }

                    // Create thumbnail image
                    await ContentService.CreateThumbnail(targetFilePath, contentType);

                    // Send progress after thumbnail creation
                    try
                    {
                        await MessageService.SendFrontendMessage("ImportProgress", new
                        {
                            id = importId,
                            progress = progressPercent + (75.0 / selectedFiles.Length),
                            fileName = originalFileName,
                            totalFiles = selectedFiles.Length,
                            currentFileIndex = i + 1,
                            status = "importing"
                        });
                    }
                    catch (Exception msgEx)
                    {
                        Log.Warning($"Failed to send import progress message: {msgEx.Message}");
                    }

                    // Create waveform data asynchronously
                    _ = Task.Run(async () => await ContentService.CreateWaveformFile(targetFilePath, contentType));

                    importedCount++;
                    Log.Information($"Successfully imported {originalFileName}");
                }
                catch (Exception ex)
                {
                    failedCount++;
                    Log.Error(ex, $"Failed to import {originalFileName}");

                    // Send error progress update
                    double progressPercent = (double)i / selectedFiles.Length * 100;
                    try
                    {
                        await MessageService.SendFrontendMessage("ImportProgress", new
                        {
                            id = importId,
                            progress = progressPercent + (100.0 / selectedFiles.Length),
                            fileName = originalFileName,
                            totalFiles = selectedFiles.Length,
                            currentFileIndex = i + 1,
                            status = "error",
                            message = $"Failed to import: {ex.Message}"
                        });
                    }
                    catch (Exception msgEx)
                    {
                        Log.Warning($"Failed to send import error progress message: {msgEx.Message}");
                    }
                }
            }

            // Send final progress update
            try
            {
                await MessageService.SendFrontendMessage("ImportProgress", new
                {
                    id = importId,
                    progress = 100,
                    fileName = importedCount > 0 ? "Finished" : "Failed",
                    totalFiles = selectedFiles.Length,
                    currentFileIndex = selectedFiles.Length,
                    status = importedCount > 0 ? "done" : "error",
                    message = $"Completed: {importedCount} successful, {failedCount} failed"
                });
            }
            catch (Exception msgEx)
            {
                Log.Warning($"Failed to send final import progress message: {msgEx.Message}");
            }

            // Reload content list to include newly imported files
            await SettingsService.LoadContentFromFolderIntoState();

            // No need for completion modal since progress cards show completion status
            Log.Information($"Import process completed: {importedCount} successful, {failedCount} failed");
        }
    }
}
