using System.Diagnostics;
using System.Globalization;
using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Services;
using Serilog;
using static Segra.Backend.Utils.GeneralUtils;

namespace Segra.Backend.Media
{
    public static class ClipService
    {
        // Dictionary to store active FFmpeg processes
        private static readonly Dictionary<int, List<Process>> ActiveFFmpegProcesses = new Dictionary<int, List<Process>>();
        // Lock for thread safety
        private static readonly object ProcessLock = new object();

        public static async Task CreateAiClipFromBookmarks(List<Bookmark> bookmarks, AiProgressMessage aiProgressMessage)
        {
            // Convert bookmarks to initial selections with buffer times
            List<Selection> initialSelections = new List<Selection>();

            foreach (var bookmark in bookmarks)
            {
                // Calculate start and end times (5 seconds before and after the bookmark time)
                // TODO (os): add so the AI calculates the added time before and after
                double startTime = Math.Max(0, bookmark.Time.TotalSeconds - 5); // Ensure not negative
                double endTime = bookmark.Time.TotalSeconds + 5;

                initialSelections.Add(new Selection
                {
                    Type = aiProgressMessage.Content.Type.ToString(),
                    StartTime = startTime,
                    EndTime = endTime,
                    FileName = aiProgressMessage.Content.FileName,
                    Game = aiProgressMessage.Content.Game
                });
            }

            // Merge overlapping selections
            List<Selection> mergedSelections = MergeOverlappingSelections(initialSelections);

            Log.Information($"Merged {initialSelections.Count} bookmarks into {mergedSelections.Count} clip sections");

            await CreateClips(mergedSelections, false, aiProgressMessage);
        }

        public static async Task CreateClips(List<Selection> selections, bool updateFrontend = true, AiProgressMessage? aiProgressMessage = null)
        {
            int id = Guid.NewGuid().GetHashCode();
            List<string> tempClipFiles = new List<string>();
            string? concatFilePath = null;
            string? outputFilePath = null;

            try
            {
                if (updateFrontend)
                {
                    await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = 0, selections });
                }
                string videoFolder = Settings.Instance.ContentFolder;

                if (selections == null || !selections.Any())
                {
                    Log.Error("No selections provided.");
                    if (updateFrontend)
                    {
                        await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = -1, selections, error = "No selections provided" });
                    }
                    return;
                }

                double totalDuration = selections.Sum(s => s.EndTime - s.StartTime);
                if (totalDuration <= 0)
                {
                    Log.Error("Total clip duration is zero or negative.");
                    if (updateFrontend)
                    {
                        await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = -1, selections, error = "Invalid clip duration" });
                    }
                    return;
                }

                string outputFolder = Path.Combine(videoFolder, aiProgressMessage != null ? "highlights" : "clips");
                Directory.CreateDirectory(outputFolder);

                if (!FFmpegService.FFmpegExists())
                {
                    Log.Error($"FFmpeg executable not found at path: {FFmpegService.GetFFmpegPath()}");
                    if (updateFrontend)
                    {
                        await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = -1, selections, error = "FFmpeg not found" });
                    }
                    return;
                }

                double processedDuration = 0;
                foreach (var selection in selections)
                {
                    string inputFilePath = Path.Combine(videoFolder, selection.Type.ToLower() + "s", $"{selection.FileName}.mp4");
                    if (!File.Exists(inputFilePath))
                    {
                        Log.Information($"Input video file not found: {inputFilePath}");
                        continue;
                    }

                    string tempFileName = Path.Combine(Path.GetTempPath(), $"clip{Guid.NewGuid()}.mp4");
                    double clipDuration = selection.EndTime - selection.StartTime;

                    await ExtractClip(id, inputFilePath, tempFileName, selection.StartTime, selection.EndTime, progress =>
                    {
                        double currentProgress = (processedDuration + (progress * clipDuration)) / totalDuration * 96;
                        if (updateFrontend)
                        {
                            _ = MessageService.SendFrontendMessage("ClipProgress", new { id, progress = currentProgress, selections });
                        }

                        if (aiProgressMessage != null && !string.IsNullOrEmpty(aiProgressMessage.Id))
                        {
                            // Update from 80% to 98%
                            double fraction = (processedDuration + (progress * clipDuration)) / totalDuration;
                            double aiProgress = 80 + fraction * 18;
                            if (aiProgress > 98) aiProgress = 98;

                            aiProgressMessage.Progress = (int)Math.Floor(aiProgress);
                            aiProgressMessage.Message = $"Rendering clips...";
                            _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);
                        }
                    });

                    if (updateFrontend)
                    {
                        _ = MessageService.SendFrontendMessage("ClipProgress", new { id, progress = 97, selections });
                    }

                    // Verify the temp file was created successfully
                    if (!File.Exists(tempFileName))
                    {
                        Log.Error($"Failed to create temp clip file: {tempFileName}");
                        throw new Exception($"Failed to extract clip from {selection.FileName}");
                    }

                    processedDuration += clipDuration;
                    tempClipFiles.Add(tempFileName);
                }

                if (updateFrontend)
                {
                    _ = MessageService.SendFrontendMessage("ClipProgress", new { id, progress = 98, selections });
                }

                if (!tempClipFiles.Any())
                {
                    Log.Error("No valid clips were extracted.");
                    if (updateFrontend)
                    {
                        await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = -1, selections, error = "No valid clips were extracted" });
                    }
                    return;
                }

                string outputFileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
                outputFilePath = Path.Combine(outputFolder, outputFileName);

                if (aiProgressMessage != null)
                {
                    aiProgressMessage.Progress = 99;
                    aiProgressMessage.Message = "Rendering final clip...";
                    _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);
                }

                if (updateFrontend)
                {
                    _ = MessageService.SendFrontendMessage("ClipProgress", new { id, progress = 99, selections });
                }

                if (tempClipFiles.Count == 1)
                {
                    File.Move(tempClipFiles[0], outputFilePath);
                    tempClipFiles.Clear();
                }
                else
                {
                    concatFilePath = Path.Combine(Path.GetTempPath(), $"concat_list_{Guid.NewGuid()}.txt");
                    await File.WriteAllLinesAsync(concatFilePath, tempClipFiles.Select(f => $"file '{f.Replace("\\", "\\\\").Replace("'", "\\'")}"));

                    try
                    {
                        await FFmpegService.RunWithProgress(id,
                            $"-y -f concat -safe 0 -i \"{concatFilePath}\" -c copy -movflags +faststart \"{outputFilePath}\"",
                            totalDuration,
                            progress => { },
                            process =>
                            {
                                // Track the concatenation process so it can be cancelled
                                lock (ProcessLock)
                                {
                                    if (!ActiveFFmpegProcesses.ContainsKey(id))
                                    {
                                        ActiveFFmpegProcesses[id] = new List<Process>();
                                    }
                                    ActiveFFmpegProcesses[id].Add(process);
                                    Log.Information($"[Clip {id}] Tracking concatenation FFmpeg process (PID: {process.Id})");
                                }
                            }
                        );
                    }
                    finally
                    {
                        // Clean up the process from tracking after completion or error
                        lock (ProcessLock)
                        {
                            ActiveFFmpegProcesses.Remove(id);
                            Log.Information($"[Clip {id}] Removed concatenation process from active processes");
                        }
                    }
                }

                // Verify the output file was created successfully
                if (!File.Exists(outputFilePath))
                {
                    throw new Exception("Failed to create final clip file");
                }

                // Ensure file is fully written to disk/network before thumbnail generation (required for network drives)
                await EnsureFileReady(outputFilePath);

                // Finalization
                await ContentService.CreateMetadataFile(outputFilePath, aiProgressMessage != null ? Content.ContentType.Highlight : Content.ContentType.Clip, selections.FirstOrDefault()?.Game!, null, selections.FirstOrDefault()?.Title);
                await ContentService.CreateThumbnail(outputFilePath, aiProgressMessage != null ? Content.ContentType.Highlight : Content.ContentType.Clip);
                await ContentService.CreateWaveformFile(outputFilePath, aiProgressMessage != null ? Content.ContentType.Highlight : Content.ContentType.Clip);
                await SettingsService.LoadContentFromFolderIntoState();
                if (updateFrontend)
                {
                    await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = 100, selections });
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Clip {id}] Error creating clip: {ex.Message}");
                Log.Error($"[Clip {id}] Stack trace: {ex.StackTrace}");

                // Clean up any partially created output file
                if (!string.IsNullOrEmpty(outputFilePath))
                {
                    SafeDelete(outputFilePath);
                }

                // Notify frontend of failure
                if (updateFrontend)
                {
                    await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = -1, selections, error = ex.Message });
                }

                if (aiProgressMessage != null)
                {
                    aiProgressMessage.Progress = -1;
                    aiProgressMessage.Message = $"Failed to create clip: {ex.Message}";
                    await MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);
                }
            }
            finally
            {
                // Always cleanup temp files
                tempClipFiles.ForEach(SafeDelete);
                if (!string.IsNullOrEmpty(concatFilePath))
                {
                    SafeDelete(concatFilePath);
                }
            }
        }

        public static async Task<string?> CreateAiClipToAnalyzeFromBookmark(Bookmark bookmark, Content content)
        {
            // Calculate start and end times (10 seconds before and 10 seconds after the bookmark time) for the Ai to analyze
            Log.Information("Creating AI clip to analyze for " + bookmark);
            double startTime = Math.Max(0, bookmark.Time.TotalSeconds - 10); // Ensure not negative
            double endTime = bookmark.Time.TotalSeconds + 10; // TODO (os): what happens if this is longer than the clip if it's right at the end?

            string videoFolder = Settings.Instance.ContentFolder;

            // Create .ai directory if it doesn't exist
            string aiOutputFolder = Path.Combine(videoFolder, ".ai");
            DirectoryInfo dir = Directory.CreateDirectory(aiOutputFolder);
            dir.Attributes |= FileAttributes.Hidden;

            string inputFilePath = Path.Combine(videoFolder, content.Type.ToString().ToLower() + "s", $"{content.FileName}.mp4");
            if (!File.Exists(inputFilePath))
            {
                Log.Error($"Input video file not found: {inputFilePath}");
                return null;
            }

            string outputFileName = $"{content.FileName}_{bookmark.Type}_{bookmark.Id}.mp4";
            string outputFilePath = Path.Combine(aiOutputFolder, outputFileName);

            if (!FFmpegService.FFmpegExists())
            {
                Log.Error($"FFmpeg executable not found at path: {FFmpegService.GetFFmpegPath()}");
                return null;
            }

            double clipDuration = endTime - startTime;

            // 1) Extract WITHOUT re-encoding to a temp file (fastest way)
            string tempFilePath = Path.GetTempFileName().Replace(".tmp", ".mp4");
            string copyArguments =
                $"-y -ss {startTime.ToString(CultureInfo.InvariantCulture)} " +
                $"-t {clipDuration.ToString(CultureInfo.InvariantCulture)} " +
                $"-i \"{inputFilePath}\" " +
                $"-c copy -movflags +faststart \"{tempFilePath}\"";

            await FFmpegService.RunSimple(copyArguments);

            var fileInfo = new FileInfo(tempFilePath);
            const long oneGB = 1L << 30; // 1GB in bytes

            // The file limit is 1GB, so if it's larger, we need to compress it (this takes longer time)
            if (fileInfo.Length > oneGB)
            {
                Log.Information($"Temp file is larger than 1GB ({fileInfo.Length} bytes). Re-encoding...");

                string reencodeArguments =
                    $"-y -i \"{tempFilePath}\" " +
                    $"-c:v libx264 -preset veryfast -crf 28 " +
                    $"-c:a aac -b:a 128k -movflags +faststart \"{outputFilePath}\"";

                await FFmpegService.RunSimple(reencodeArguments);

                // After re-encode is done, we no longer need the temp file and the compressed file is already at the final output path
                SafeDelete(tempFilePath);
            }
            else
            {
                // If the file is <= 1GB, just move it to the final output path
                SafeDelete(outputFilePath);
                File.Move(tempFilePath, outputFilePath);
            }

            // Return the generated filepath so it can be tracked
            return outputFilePath;
        }

        private static List<Selection> MergeOverlappingSelections(List<Selection> selections)
        {
            // Sort selections by start time
            var sortedSelections = selections.OrderBy(s => s.StartTime).ToList();
            List<Selection> mergedSelections = new List<Selection>();

            // Start with the first selection
            Selection current = sortedSelections[0];

            // Iterate through the sorted selections
            for (int i = 1; i < sortedSelections.Count; i++)
            {
                var next = sortedSelections[i];

                // Check if the current selection overlaps with the next one
                if (current.EndTime >= next.StartTime)
                {
                    // Merge by extending the end time if needed
                    current.EndTime = Math.Max(current.EndTime, next.EndTime);
                }
                else
                {
                    // No overlap, add current to result and move to next
                    mergedSelections.Add(current);
                    current = next;
                }
            }

            // Add the last merged selection
            mergedSelections.Add(current);

            return mergedSelections;
        }


        private static async Task ExtractClip(int clipId, string inputFilePath, string outputFilePath, double startTime, double endTime,
                            Action<double> progressCallback)
        {
            double duration = endTime - startTime;
            var settings = Settings.Instance;

            string videoCodec;
            string qualityArgs;
            string presetArgs;
            if (settings.ClipEncoder.Equals("gpu", StringComparison.OrdinalIgnoreCase))
            {
                // GPU encoder uses hardware-accelerated codecs based on GPU vendor
                GpuVendor gpuVendor = DetectGpuVendor();

                switch (gpuVendor)
                {
                    case GpuVendor.Nvidia:
                        if (settings.ClipCodec.Equals("h265", StringComparison.OrdinalIgnoreCase))
                            videoCodec = "hevc_nvenc";
                        else if (settings.ClipCodec.Equals("av1", StringComparison.OrdinalIgnoreCase))
                            videoCodec = "av1_nvenc";
                        else
                            videoCodec = "h264_nvenc";

                        // NVENC uses -cq for quality control and specific presets
                        qualityArgs = $"-cq {settings.ClipQualityGpu}";
                        presetArgs = $"-preset {settings.ClipPreset}";
                        break;

                    case GpuVendor.AMD:
                        if (settings.ClipCodec.Equals("h265", StringComparison.OrdinalIgnoreCase))
                            videoCodec = "hevc_amf";
                        else if (settings.ClipCodec.Equals("av1", StringComparison.OrdinalIgnoreCase))
                            videoCodec = "av1_amf";
                        else
                            videoCodec = "h264_amf";

                        // AMF uses -rc cqp (Constant QP) rate control with -qp_i, -qp_p for quality control
                        qualityArgs = $"-rc cqp -qp_i {settings.ClipQualityGpu} -qp_p {settings.ClipQualityGpu}";
                        // Frontend sends AMD AMF usage modes directly: quality, transcoding, lowlatency, ultralowlatency
                        presetArgs = $"-usage {settings.ClipPreset}";
                        break;

                    case GpuVendor.Intel:
                        if (settings.ClipCodec.Equals("h265", StringComparison.OrdinalIgnoreCase))
                            videoCodec = "hevc_qsv";
                        else if (settings.ClipCodec.Equals("av1", StringComparison.OrdinalIgnoreCase))
                            videoCodec = "av1_qsv";
                        else
                            videoCodec = "h264_qsv";

                        // QSV uses -global_quality for ICQ mode
                        qualityArgs = $"-global_quality {settings.ClipQualityGpu}";
                        presetArgs = $"-preset {settings.ClipPreset}";
                        break;

                    default:
                        // Fall back to CPU encoding if GPU vendor is unknown
                        Log.Warning("Unknown GPU vendor detected, falling back to CPU encoding");
                        if (settings.ClipCodec.Equals("h265", StringComparison.OrdinalIgnoreCase))
                            videoCodec = "libx265";
                        else
                            videoCodec = "libx264";

                        // CPU codecs use -crf and standard presets
                        qualityArgs = $"-crf {settings.ClipQualityCpu}";
                        presetArgs = $"-preset {settings.ClipPreset}";
                        break;
                }
            }
            else
            {
                // CPU encoder uses software codecs
                if (settings.ClipCodec.Equals("h265", StringComparison.OrdinalIgnoreCase))
                    videoCodec = "libx265";
                else
                    videoCodec = "libx264";

                // CPU codecs use -crf and standard presets
                qualityArgs = $"-crf {settings.ClipQualityCpu}";
                presetArgs = $"-preset {settings.ClipPreset}";
            }

            string fpsArg = settings.ClipFps > 0 ? $"-r {settings.ClipFps}" : "";

            string arguments = $"-y -ss {startTime.ToString(CultureInfo.InvariantCulture)} -t {duration.ToString(CultureInfo.InvariantCulture)} " +
                             $"-i \"{inputFilePath}\" -c:v {videoCodec} {presetArgs} {qualityArgs} {fpsArg} " +
                             $"-c:a aac -b:a {settings.ClipAudioQuality} -movflags +faststart \"{outputFilePath}\"";
            Log.Information("Extracting clip");
            Log.Information($"FFmpeg arguments: {arguments}");

            try
            {
                await FFmpegService.RunWithProgress(clipId, arguments, duration, progressCallback, process =>
                {
                    // Track the process so it can be cancelled
                    lock (ProcessLock)
                    {
                        if (!ActiveFFmpegProcesses.ContainsKey(clipId))
                        {
                            ActiveFFmpegProcesses[clipId] = new List<Process>();
                        }
                        ActiveFFmpegProcesses[clipId].Add(process);
                        Log.Information($"[Clip {clipId}] Tracking FFmpeg process (PID: {process.Id})");
                    }
                });
            }
            finally
            {
                // Clean up the process from tracking after completion or error
                lock (ProcessLock)
                {
                    ActiveFFmpegProcesses.Remove(clipId);
                    Log.Information($"[Clip {clipId}] Removed from active processes");
                }
            }
        }


        public static async void CancelClip(int clipId)
        {
            Log.Information($"[Clip {clipId}] Cancel requested");

            bool wasCancelled = false;

            lock (ProcessLock)
            {
                if (ActiveFFmpegProcesses.TryGetValue(clipId, out var processes))
                {
                    Log.Information($"[Clip {clipId}] Found {processes.Count} active process(es) to cancel");

                    foreach (var process in processes.ToList())
                    {
                        try
                        {
                            int processId = process.Id; // Capture ID before killing

                            if (!process.HasExited)
                            {
                                Log.Information($"[Clip {clipId}] Killing FFmpeg process (PID: {processId})");
                                process.Kill(true); // Force kill the process and child processes
                                Log.Information($"[Clip {clipId}] Successfully killed process (PID: {processId})");
                            }
                            else
                            {
                                Log.Information($"[Clip {clipId}] Process (PID: {processId}) already exited");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[Clip {clipId}] Error killing FFmpeg process: {ex.Message}");
                        }
                    }

                    ActiveFFmpegProcesses.Remove(clipId);
                    Log.Information($"[Clip {clipId}] Removed from active processes after cancellation");
                    wasCancelled = true;
                }
                else
                {
                    Log.Warning($"[Clip {clipId}] No active processes found to cancel (may have already completed)");
                }
            }

            if (wasCancelled)
            {
                await MessageService.SendFrontendMessage("ClipProgress", new { id = clipId, progress = 100, selections = new List<Selection>() });
            }
        }

        private static void SafeDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { Log.Information($"Error deleting file {path}: {ex.Message}"); }
        }
    }
}
