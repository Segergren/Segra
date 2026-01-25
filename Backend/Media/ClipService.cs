using System.Diagnostics;
using System.Globalization;
using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Services;
using Segra.Backend.Shared;
using Segra.Backend.Windows.Storage;
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

        public static async Task CreateClips(List<Selection> selections)
        {
            int id = Guid.NewGuid().GetHashCode();
            List<string> tempClipFiles = new List<string>();
            string? concatFilePath = null;
            string? outputFilePath = null;

            try
            {
                await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = 0, selections });
                string videoFolder = Settings.Instance.ContentFolder;

                if (selections == null || !selections.Any())
                {
                    Log.Error("No selections provided.");
                    await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = -1, selections, error = "No selections provided" });
                    return;
                }

                double totalDuration = selections.Sum(s => s.EndTime - s.StartTime);
                if (totalDuration <= 0)
                {
                    Log.Error("Total clip duration is zero or negative.");
                    await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = -1, selections, error = "Invalid clip duration" });
                    return;
                }

                // Use game from first selection for the output folder
                var firstSelection = selections.FirstOrDefault();
                string outputGameFolder = StorageService.SanitizeGameNameForFolder(firstSelection?.Game ?? "Unknown");
                string outputFolder = Path.Combine(videoFolder, FolderNames.Clips, outputGameFolder);
                Directory.CreateDirectory(outputFolder);

                if (!FFmpegService.FFmpegExists())
                {
                    Log.Error($"FFmpeg executable not found at path: {FFmpegService.GetFFmpegPath()}");
                    await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = -1, selections, error = "FFmpeg not found" });
                    return;
                }

                double processedDuration = 0;
                foreach (var selection in selections)
                {
                    // Input files are organized by game
                    string inputGameFolder = StorageService.SanitizeGameNameForFolder(selection.Game);
                    var selectionType = Enum.Parse<Content.ContentType>(selection.Type);
                    string inputFolderName = FolderNames.GetVideoFolderName(selectionType);
                    string inputFilePath = Path.Combine(videoFolder, inputFolderName, inputGameFolder, $"{selection.FileName}.mp4");
                    bool isDash = false;

                    if (!File.Exists(inputFilePath))
                    {
                        // Check for DASH manifest
                        string dashFilePath = Path.Combine(videoFolder, inputFolderName, inputGameFolder, $"{selection.FileName}.mpd");
                        if (File.Exists(dashFilePath))
                        {
                            inputFilePath = dashFilePath;
                            isDash = true;
                        }
                        else
                        {
                            Log.Information($"Input video file not found: {inputFilePath}");
                            continue;
                        }
                    }

                    string tempFileName = Path.Combine(Path.GetTempPath(), $"clip{Guid.NewGuid()}.mp4");
                    double clipDuration = selection.EndTime - selection.StartTime;

                    await ExtractClip(id, inputFilePath, tempFileName, selection.StartTime, selection.EndTime, progress =>
                    {
                        double clampedProgress = Math.Min(progress, 1.0);
                        double currentProgress = (processedDuration + (clampedProgress * clipDuration)) / totalDuration * 95;
                        _ = MessageService.SendFrontendMessage("ClipProgress", new { id, progress = currentProgress, selections });
                    }, isDash);

                    // Verify the temp file was created successfully
                    if (!File.Exists(tempFileName))
                    {
                        Log.Error($"Failed to create temp clip file: {tempFileName}");
                        throw new Exception($"Failed to extract clip from {selection.FileName}");
                    }

                    processedDuration += clipDuration;
                    tempClipFiles.Add(tempFileName);
                }

                if (!tempClipFiles.Any())
                {
                    Log.Error("No valid clips were extracted.");
                    await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = -1, selections, error = "No valid clips were extracted" });
                    return;
                }

                _ = MessageService.SendFrontendMessage("ClipProgress", new { id, progress = 96, selections });

                string outputFileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
                outputFilePath = Path.Combine(outputFolder, outputFileName);

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
                        lock (ProcessLock)
                        {
                            ActiveFFmpegProcesses.Remove(id);
                        }
                    }
                }

                _ = MessageService.SendFrontendMessage("ClipProgress", new { id, progress = 97, selections });

                if (!File.Exists(outputFilePath))
                {
                    throw new Exception("Failed to create final clip file");
                }

                await EnsureFileReady(outputFilePath);

                _ = MessageService.SendFrontendMessage("ClipProgress", new { id, progress = 98, selections });

                await ContentService.CreateMetadataFile(outputFilePath, Content.ContentType.Clip, firstSelection?.Game!, null, firstSelection?.Title, igdbId: firstSelection?.IgdbId);
                await ContentService.CreateThumbnail(outputFilePath, Content.ContentType.Clip);
                await ContentService.CreateWaveformFile(outputFilePath, Content.ContentType.Clip);

                _ = MessageService.SendFrontendMessage("ClipProgress", new { id, progress = 99, selections });

                await SettingsService.LoadContentFromFolderIntoState();
                await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = 100, selections });
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
                await MessageService.SendFrontendMessage("ClipProgress", new { id, progress = -1, selections, error = ex.Message });
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

        private static async Task ExtractClip(int clipId, string inputFilePath, string outputFilePath, double startTime, double endTime,
                            Action<double> progressCallback, bool isDash)
        {
            double duration = endTime - startTime;
            string arguments;
            string tempManifestPath = "";

            try
            {
                if (isDash)
                {
                    // Copy the manifest to a temp file to avoid race conditions with OBS writing to it
                    // The .m4s segments are relative, so the temp manifest must be in the same folder
                    string folder = Path.GetDirectoryName(inputFilePath)!;
                    tempManifestPath = Path.Combine(folder, $"temp_{Guid.NewGuid()}.mpd");
                    File.Copy(inputFilePath, tempManifestPath, true);
                    inputFilePath = tempManifestPath;

                    // Use stream copy for DASH input
                    // We use output seeking (-ss after -i) because input seeking on a rolling-window live DASH manifest
                    // often fails or seeks to the wrong relative time. Output seeking decodes/discards packets until the timestamp.
                    arguments = $"-y -i \"{inputFilePath}\" -ss {startTime.ToString(CultureInfo.InvariantCulture)} " +
                                $"-t {duration.ToString(CultureInfo.InvariantCulture)} -c copy -movflags +faststart \"{outputFilePath}\"";
                }
                else
                {
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

                    arguments = $"-y -ss {startTime.ToString(CultureInfo.InvariantCulture)} -t {duration.ToString(CultureInfo.InvariantCulture)} " +
                                     $"-i \"{inputFilePath}\" -c:v {videoCodec} {presetArgs} {qualityArgs} {fpsArg} " +
                                     $"-c:a aac -b:a {settings.ClipAudioQuality} -movflags +faststart \"{outputFilePath}\"";
                }

                Log.Information("Extracting clip");
                Log.Information($"FFmpeg arguments: {arguments}");

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

                // Cleanup temp manifest if used
                if (!string.IsNullOrEmpty(tempManifestPath))
                {
                    SafeDelete(tempManifestPath);
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
