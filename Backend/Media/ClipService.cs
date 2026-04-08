using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

                // Read per-selection audio track names and build union layout
                bool anySelectionHasMutedTracks = selections.Any(s => s.MutedAudioTracks != null && s.MutedAudioTracks.Count > 0);
                var perSelectionTrackNames = new List<List<string>?>();
                if (Settings.Instance.ClipKeepSeparateAudioTracks || anySelectionHasMutedTracks)
                {
                    foreach (var sel in selections)
                        perSelectionTrackNames.Add(GetSourceAudioTrackNames(sel));
                }
                else
                {
                    perSelectionTrackNames.AddRange(Enumerable.Repeat<List<string>?>(null, selections.Count));
                }

                // Union of all track names across sources -- used to normalise every temp clip to the same stream layout
                List<string>? unionAudioLayout = Settings.Instance.ClipKeepSeparateAudioTracks
                    ? BuildUnionAudioLayout(perSelectionTrackNames)
                    : null;

                double processedDuration = 0;
                int selectionIndex = 0;
                foreach (var selection in selections)
                {
                    // Use the actual file path from metadata when available, fall back to reconstructed path
                    string inputFilePath;
                    if (!string.IsNullOrEmpty(selection.FilePath) && File.Exists(selection.FilePath))
                    {
                        inputFilePath = selection.FilePath;
                    }
                    else
                    {
                        string inputGameFolder = StorageService.SanitizeGameNameForFolder(selection.Game);
                        var selectionType = Enum.Parse<Content.ContentType>(selection.Type);
                        string inputFolderName = FolderNames.GetVideoFolderName(selectionType);
                        inputFilePath = Path.Combine(videoFolder, inputFolderName, inputGameFolder, $"{selection.FileName}.mp4");
                    }
                    if (!File.Exists(inputFilePath))
                    {
                        Log.Information($"Input video file not found: {inputFilePath}");
                        selectionIndex++;
                        continue;
                    }

                    string tempFileName = Path.Combine(Path.GetTempPath(), $"clip{Guid.NewGuid()}.mp4");
                    double clipDuration = selection.EndTime - selection.StartTime;

                    List<string>? selectionTrackNames = perSelectionTrackNames[selectionIndex];
                    List<string>? targetLayout = Settings.Instance.ClipKeepSeparateAudioTracks ? unionAudioLayout : null;

                    await ExtractClip(id, inputFilePath, tempFileName, selection.StartTime, selection.EndTime, selectionTrackNames, selection.MutedAudioTracks, selection.AudioTrackVolumes, targetLayout, progress =>
                    {
                        double clampedProgress = Math.Min(progress, 1.0);
                        double currentProgress = (processedDuration + (clampedProgress * clipDuration)) / totalDuration * 95;
                        _ = MessageService.SendFrontendMessage("ClipProgress", new { id, progress = currentProgress, selections });
                    });

                    // Verify the temp file was created successfully
                    if (!File.Exists(tempFileName))
                    {
                        Log.Error($"Failed to create temp clip file: {tempFileName}");
                        throw new Exception($"Failed to extract clip from {selection.FileName}");
                    }

                    processedDuration += clipDuration;
                    tempClipFiles.Add(tempFileName);
                    selectionIndex++;
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
                        string mapAllArg = unionAudioLayout != null ? "-map 0 " : "";
                        string concatMetadataArgs = "";
                        // When multi-track is active, re-encode audio during concat to fix
                        // DTS misalignment between streams (different seek offsets cause
                        // slightly different AAC frame counts per stream).
                        // Video is always stream-copied.
                        string codecArg = unionAudioLayout != null
                            ? $"-c:v copy -c:a aac -b:a {Settings.Instance.ClipAudioQuality} "
                            : "-c copy ";
                        if (unionAudioLayout != null)
                        {
                            concatMetadataArgs = string.Join(" ", unionAudioLayout.Select((name, i) => $"-metadata:s:a:{i} title=\"{name}\"")) + " ";
                        }
                        await FFmpegService.RunWithProgress(id,
                            $"-y -f concat -safe 0 -i \"{concatFilePath}\" {mapAllArg}{codecArg}{concatMetadataArgs}-avoid_negative_ts make_zero -movflags +faststart \"{outputFilePath}\"",
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

                await ContentService.CreateMetadataFile(outputFilePath, Content.ContentType.Clip, firstSelection?.Game!, null, firstSelection?.Title, igdbId: firstSelection?.IgdbId, audioTrackNames: unionAudioLayout);
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
                            List<string>? audioTrackNames, List<int>? mutedAudioTracks, Dictionary<int, double>? audioTrackVolumes, List<string>? targetAudioLayout, Action<double> progressCallback)
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

            // Build audio mapping, filter, and metadata based on per-selection muted tracks
            string mapArgs = "";
            string metadataArgs = "";
            string filterArgs = "";
            string extraInputArgs = "";

            if (targetAudioLayout != null && targetAudioLayout.Count > 0)
            {
                // Normalise output to the union layout so every temp clip has identical stream layout.
                // Silent (muted/missing) tracks use separate -f lavfi inputs so they are never mixed
                // with real decoded streams inside filter_complex -- mixing synthetic sources and real
                // streams in the same filtergraph causes a scheduler deadlock in FFmpeg.
                var filterParts = new List<string>();
                var mapParts = new List<string> { "-map 0:v:0" };
                var metaParts = new List<string>();
                var extraInputParts = new List<string>();
                int silenceInputIdx = 1; // lavfi inputs start at 1 (0 is the main file)

                bool sourceHasIndividualTracks = audioTrackNames != null && audioTrackNames.Count > 1;

                // Enabled individual source tracks (index > 0, not muted)
                var enabledSourceTracks = new List<int>();
                if (sourceHasIndividualTracks)
                {
                    for (int i = 1; i < audioTrackNames!.Count; i++)
                    {
                        if (mutedAudioTracks == null || !mutedAudioTracks.Contains(i))
                            enabledSourceTracks.Add(i);
                    }
                }

                // Pre-compute which individual track positions map to an enabled source stream
                var indivSourceMap = new Dictionary<int, int>(); // layout position j -> source audio index
                if (sourceHasIndividualTracks)
                {
                    for (int j = 1; j < targetAudioLayout.Count; j++)
                    {
                        int srcIdx = audioTrackNames!.FindIndex(n => string.Equals(n, targetAudioLayout[j], StringComparison.OrdinalIgnoreCase));
                        if (srcIdx > 0 && (mutedAudioTracks == null || !mutedAudioTracks.Contains(srcIdx)))
                            indivSourceMap[j] = srcIdx;
                    }
                }

                // Count how many times each source audio stream index is referenced
                var refCount = new Dictionary<int, int>();
                foreach (int i in enabledSourceTracks)
                {
                    refCount.TryGetValue(i, out int c);
                    refCount[i] = c + 1;
                }
                foreach (int srcIdx in indivSourceMap.Values)
                {
                    refCount.TryGetValue(srcIdx, out int c);
                    refCount[srcIdx] = c + 1;
                }

                // Build asplit filters for streams referenced more than once
                var available = new Dictionary<int, Queue<string>>();
                foreach (var (srcIdx, count) in refCount)
                {
                    if (count <= 1)
                    {
                        available[srcIdx] = new Queue<string>(new[] { $"[0:a:{srcIdx}]" });
                    }
                    else
                    {
                        var labels = Enumerable.Range(0, count).Select(k => $"[split_{srcIdx}_{k}]").ToList();
                        filterParts.Add($"[0:a:{srcIdx}]asplit={count}{string.Join("", labels)}");
                        available[srcIdx] = new Queue<string>(labels);
                    }
                }

                string durationStr = duration.ToString(CultureInfo.InvariantCulture);
                string silenceInput = $"-f lavfi -t {durationStr} -i \"anullsrc=cl=stereo:r=48000\"";
                string atrim = $"atrim=end={durationStr},asetpts=PTS-STARTPTS";

                // Position 0: Full Mix
                if (!sourceHasIndividualTracks)
                {
                    // No individual track metadata -- pass through the source's default audio
                    filterParts.Add($"[0:a:0]{atrim}[out_a0]");
                    mapParts.Add("-map \"[out_a0]\"");
                }
                else if (enabledSourceTracks.Count == 0)
                {
                    extraInputParts.Add(silenceInput);
                    filterParts.Add($"[{silenceInputIdx}:a:0]{atrim}[out_a0]");
                    mapParts.Add("-map \"[out_a0]\"");
                    silenceInputIdx++;
                }
                else if (enabledSourceTracks.Count == 1)
                {
                    int trackIdx = enabledSourceTracks[0];
                    string volFilter = WithAtrim(GetVolumeFilter(audioTrackVolumes, trackIdx), durationStr);
                    filterParts.Add($"{available[trackIdx].Dequeue()}{volFilter}[out_a0]");
                    mapParts.Add("-map \"[out_a0]\"");
                }
                else
                {
                    // Apply per-track volume before mixing
                    var mixInputLabels = new List<string>();
                    foreach (int i in enabledSourceTracks)
                    {
                        double vol = GetTrackVolume(audioTrackVolumes, i);
                        string srcLabel = available[i].Dequeue();
                        if (Math.Abs(vol - 1.0) > 0.001)
                        {
                            string volLabel = $"[vol_mix_{i}]";
                            filterParts.Add($"{srcLabel}volume={vol.ToString(CultureInfo.InvariantCulture)}{volLabel}");
                            mixInputLabels.Add(volLabel);
                        }
                        else
                        {
                            mixInputLabels.Add(srcLabel);
                        }
                    }
                    string inputs = string.Join("", mixInputLabels);
                    filterParts.Add($"{inputs}amix=inputs={enabledSourceTracks.Count}:duration=longest,{atrim}[out_a0]");
                    mapParts.Add("-map \"[out_a0]\"");
                }
                metaParts.Add($"-metadata:s:a:0 title=\"{targetAudioLayout[0]}\"");

                // Positions 1+: individual tracks aligned by name
                for (int j = 1; j < targetAudioLayout.Count; j++)
                {
                    if (indivSourceMap.TryGetValue(j, out int srcIdx))
                    {
                        string volFilter = WithAtrim(GetVolumeFilter(audioTrackVolumes, srcIdx), durationStr);
                        filterParts.Add($"{available[srcIdx].Dequeue()}{volFilter}[out_a{j}]");
                        mapParts.Add($"-map \"[out_a{j}]\"");
                    }
                    else
                    {
                        extraInputParts.Add(silenceInput);
                        filterParts.Add($"[{silenceInputIdx}:a:0]{atrim}[out_a{j}]");
                        mapParts.Add($"-map \"[out_a{j}]\"");
                        silenceInputIdx++;
                    }
                    metaParts.Add($"-metadata:s:a:{j} title=\"{targetAudioLayout[j]}\"");
                }

                filterArgs = filterParts.Count > 0 ? $"-filter_complex \"{string.Join(";", filterParts)}\" " : "";
                extraInputArgs = extraInputParts.Count > 0 ? string.Join(" ", extraInputParts) + " " : "";
                mapArgs = string.Join(" ", mapParts) + " ";
                metadataArgs = string.Join(" ", metaParts) + " ";
            }
            else
            {
                // Legacy paths (keepSeparate=false or no track metadata)
                bool hasMutedTracks = mutedAudioTracks != null && mutedAudioTracks.Count > 0 && audioTrackNames != null && audioTrackNames.Count > 1;
                bool hasVolumeChanges = audioTrackVolumes != null && audioTrackVolumes.Any(kv => Math.Abs(kv.Value - 1.0) > 0.001);
                bool needsAudioProcessing = hasMutedTracks || (hasVolumeChanges && audioTrackNames != null && audioTrackNames.Count > 1);

                if (needsAudioProcessing && audioTrackNames != null)
                {
                    var enabledTracks = new List<int>();
                    for (int i = 1; i < audioTrackNames.Count; i++)
                    {
                        if (mutedAudioTracks == null || !mutedAudioTracks.Contains(i))
                            enabledTracks.Add(i);
                    }

                    if (enabledTracks.Count > 0)
                    {
                        bool anyVolChange = enabledTracks.Any(i => Math.Abs(GetTrackVolume(audioTrackVolumes, i) - 1.0) > 0.001);

                        if (enabledTracks.Count == 1 && !anyVolChange)
                        {
                            mapArgs = $"-map 0:v:0 -map 0:a:{enabledTracks[0]} ";
                        }
                        else
                        {
                            var filterPartsList = new List<string>();
                            var mixInputLabels = new List<string>();

                            foreach (int i in enabledTracks)
                            {
                                double vol = GetTrackVolume(audioTrackVolumes, i);
                                if (Math.Abs(vol - 1.0) > 0.001)
                                {
                                    filterPartsList.Add($"[0:a:{i}]volume={vol.ToString(CultureInfo.InvariantCulture)}[vol_{i}]");
                                    mixInputLabels.Add($"[vol_{i}]");
                                }
                                else
                                {
                                    mixInputLabels.Add($"[0:a:{i}]");
                                }
                            }

                            if (enabledTracks.Count == 1)
                            {
                                // Single track with volume change
                                filterArgs = $"-filter_complex \"{string.Join(";", filterPartsList)}\" ";
                                mapArgs = $"-map 0:v:0 -map \"{mixInputLabels[0]}\" ";
                            }
                            else
                            {
                                string allInputs = string.Join("", mixInputLabels);
                                filterPartsList.Add($"{allInputs}amix=inputs={enabledTracks.Count}:duration=longest[mix]");
                                filterArgs = $"-filter_complex \"{string.Join(";", filterPartsList)}\" ";
                                mapArgs = "-map 0:v:0 -map \"[mix]\" ";
                            }
                        }

                        metadataArgs = "-metadata:s:a:0 title=\"Full Mix\" ";
                    }
                }
                else if (settings.ClipKeepSeparateAudioTracks && audioTrackNames != null)
                {
                    mapArgs = "-map 0:v:0 -map 0:a ";
                    for (int i = 0; i < audioTrackNames.Count; i++)
                        metadataArgs += $"-metadata:s:a:{i} title=\"{audioTrackNames[i]}\" ";
                }
            }

            string verboseFlag = filterArgs.Length > 0 || extraInputArgs.Length > 0 ? "-v verbose " : "";
            // When using the union layout, force a consistent sample rate on every output audio stream.
            // Source tracks may be 44.1 kHz while the anullsrc silence inputs are 48 kHz, so without this
            // each temp clip's per-slot sample rate depends on whether the slot got real audio or silence.
            // The concat demuxer then uses the first clip's stream params for subsequent clips, playing
            // mismatched samples at the wrong rate (the reported "shrunken audio").
            string audioRateArg = targetAudioLayout != null ? "-ar 48000 " : "";
            string arguments = $"-y {verboseFlag}-ss {startTime.ToString(CultureInfo.InvariantCulture)} -t {duration.ToString(CultureInfo.InvariantCulture)} " +
                             $"-i \"{inputFilePath}\" {extraInputArgs}{filterArgs}{mapArgs}-c:v {videoCodec} {presetArgs} {qualityArgs} {fpsArg} " +
                             $"-c:a aac -b:a {settings.ClipAudioQuality} {audioRateArg}{metadataArgs}-t {duration.ToString(CultureInfo.InvariantCulture)} -movflags +faststart \"{outputFilePath}\"";
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

        private static List<string>? BuildUnionAudioLayout(List<List<string>?> perSelectionTrackNames)
        {
            var union = new List<string> { "Full Mix" };
            foreach (var trackNames in perSelectionTrackNames)
            {
                if (trackNames == null) continue;
                foreach (var name in trackNames.Skip(1))
                {
                    if (!union.Any(u => string.Equals(u, name, StringComparison.OrdinalIgnoreCase)))
                        union.Add(name);
                }
            }
            return union.Count > 1 ? union : null;
        }

        private static List<string>? GetSourceAudioTrackNames(Selection selection)
        {
            try
            {
                var contentType = Enum.Parse<Content.ContentType>(selection.Type);
                string metadataFolderPath = FolderNames.GetMetadataFolderPath(contentType);
                string metadataFilePath = Path.Combine(metadataFolderPath, $"{selection.FileName}.json");

                if (File.Exists(metadataFilePath))
                {
                    var metadataContent = File.ReadAllText(metadataFilePath);
                    var metadata = JsonSerializer.Deserialize<Content>(metadataContent);
                    if (metadata?.AudioTrackNames != null && metadata.AudioTrackNames.Count > 1)
                    {
                        return metadata.AudioTrackNames;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to read source audio track names: {ex.Message}");
            }

            return null;
        }

        private static void SafeDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { Log.Information($"Error deleting file {path}: {ex.Message}"); }
        }

        private static string WithAtrim(string filterChain, string durationStr)
        {
            if (filterChain == "acopy")
                return $"atrim=end={durationStr},asetpts=PTS-STARTPTS";
            return $"{filterChain},atrim=end={durationStr},asetpts=PTS-STARTPTS";
        }

        private static double GetTrackVolume(Dictionary<int, double>? audioTrackVolumes, int trackIndex)
        {
            if (audioTrackVolumes != null && audioTrackVolumes.TryGetValue(trackIndex, out double vol))
                return Math.Max(0, Math.Min(1, vol));
            return 1.0;
        }

        private static string GetVolumeFilter(Dictionary<int, double>? audioTrackVolumes, int trackIndex)
        {
            double vol = GetTrackVolume(audioTrackVolumes, trackIndex);
            if (Math.Abs(vol - 1.0) > 0.001)
                return $"volume={vol.ToString(CultureInfo.InvariantCulture)}";
            return "acopy";
        }
    }
}
