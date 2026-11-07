using Serilog;
using ObsKit.NET.Scenes;
using ObsKit.NET.Outputs;
using ObsKit.NET.Sources;
using ObsKit.NET.Encoders;
using Segra.Backend.Games;
using Segra.Backend.Media;
using Segra.Backend.Shared;
using ObsKit.NET.Native.Types;
using Segra.Backend.Core.Models;
using static Segra.Backend.Shared.GeneralUtils;

namespace Segra.Backend.Recorder
{
    // The webcam is recorded on its own canvas into a separate file, so the editor can show, hide or move it
    public static partial class OBSService
    {
        private const uint WebcamWidth = 1280;
        private const uint WebcamHeight = 720;
        private const int WebcamBitrateKbps = 3000;

        private static Canvas? _webcamCanvas;
        private static Scene? _webcamScene;
        private static WebcamCapture? _webcamSource;
        private static SceneItem? _webcamItem;
        private static VideoEncoder? _webcamEncoder;
        private static RecordingOutput? _webcamOutput;
        private static ReplayBuffer? _webcamBufferOutput;

        private static string WebcamFolder => PathUtils.Combine(Settings.Instance.ContentFolder, FolderNames.Webcam);

        // Call after the main video is final: the canvas copies its fps and color format
        private static void AddWebcam(string encoderId, EffectiveRecordingSettings eff)
        {
            var settings = Settings.Instance;
            if (!settings.WebcamEnabled || string.IsNullOrEmpty(settings.WebcamDeviceId))
                return;

            try
            {
                _webcamCanvas = Canvas.CreatePrivate("Segra Webcam", WebcamWidth, WebcamHeight, ObsCanvasFlags.Activate | ObsCanvasFlags.Ephemeral);
                _webcamScene = _webcamCanvas.CreateScene("Webcam Scene");

                _webcamSource = new WebcamCapture("webcam", settings.WebcamDeviceId);
                // Video only; a webcam mic would otherwise land in the mix
                _webcamSource.AudioMixers = 0;
                _webcamItem = _webcamScene.AddSource(_webcamSource);
                // Fill the 16:9 canvas and crop the camera's overflow, centered
                _webcamItem.SetBounds(ObsBoundsType.ScaleOuter, WebcamWidth, WebcamHeight).SetPosition(0, 0);
                _webcamItem.BoundsAlignment = ObsAlignment.Center;
                _webcamItem.CropToBounds = true;
                _webcamCanvas.SetScene(_webcamScene);

                using var encoderSettings = new ObsKit.NET.Core.Settings();
                ConfigureWebcamEncoder(encoderSettings, encoderId, eff);
                _webcamEncoder = new VideoEncoder(encoderId, "Segra Webcam", encoderSettings);
                Log.Information($"Webcam layer added ({encoderId}, {WebcamWidth}x{WebcamHeight})");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to add the webcam layer: {ex.Message}");
                DisposeWebcamSource();
            }
        }

        private static void ConfigureWebcamEncoder(ObsKit.NET.Core.Settings s, string encoderId, EffectiveRecordingSettings eff)
        {
            s.Set("keyint_sec", 1);
            if (IsVaapiEncoder(encoderId))
            {
                ConfigureVaapiVideoEncoder(s, encoderId, eff);
                s.Set("rate_control", "CBR");
                s.Set("bitrate", WebcamBitrateKbps);
                return;
            }

            s.Set("profile", _isHdrRecording && EncoderInfo.Get(encoderId)?.Codec == "hevc" ? "main10" : "high");
            s.Set("rate_control", "CBR");
            s.Set("bitrate", WebcamBitrateKbps);
            s.Set("max_bitrate", WebcamBitrateKbps);
            s.Set("use_bufsize", true);
            s.Set("bufsize", WebcamBitrateKbps);
            ApplyNvencBFrameLimit(s, encoderId);

            if (encoderId.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            {
                s.Set("multipass", "disabled");
                s.Set("adaptive_quantization", false);
                s.Set("lookahead", false);
            }
            else if (encoderId.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            {
                s.Set("latency", "ultra-low");
            }
        }

        private static WebcamLayer? CreateWebcamOutputs(bool buffer, bool session, string? sessionVideoPath, EffectiveRecordingSettings eff)
        {
            if (_webcamEncoder == null || _webcamCanvas == null || _audioEncoders.Count == 0)
                return null;

            var settings = Settings.Instance;
            var layer = new WebcamLayer { X = settings.WebcamX, Y = settings.WebcamY, Size = settings.WebcamSize };

            try
            {
                Directory.CreateDirectory(WebcamFolder);

                if (buffer)
                {
                    // Room for the buffer duration at the webcam bitrate, with margin
                    int maxSizeMb = Math.Max(100, eff.ReplayBufferDuration * WebcamBitrateKbps / 8 / 1024 * 2);
                    _webcamBufferOutput = new ReplayBuffer("webcam_buffer_output", eff.ReplayBufferDuration, maxSizeMb);
                    _webcamBufferOutput.SetDirectory(WebcamFolder);
                    _webcamBufferOutput.SetFilenameFormat("%CCYY-%MM-%DD_%hh-%mm-%ss_replay");
                    _webcamBufferOutput.Update(s => s.Set("extension", "mp4").Set("tracks", 1L));
                    _webcamBufferOutput.WithVideoEncoder(_webcamEncoder, _webcamCanvas);
                    // Outputs need an audio track; the full mix is already encoded
                    _webcamBufferOutput.WithAudioEncoder(_audioEncoders[0], track: 0);
                    _webcamBufferOutput.Stopped += OnWebcamOutputStopped;
                }

                if (session && sessionVideoPath != null)
                {
                    layer.FilePath = PathUtils.Combine(WebcamFolder, Path.GetFileName(sessionVideoPath));
                    _webcamOutput = new RecordingOutput("webcam_output", layer.FilePath);
                    _webcamOutput.Update(s => s.Set("tracks", 1L));
                    _webcamOutput.WithVideoEncoder(_webcamEncoder, _webcamCanvas);
                    _webcamOutput.WithAudioEncoder(_audioEncoders[0], track: 0);
                    _webcamOutput.Stopped += OnWebcamOutputStopped;
                    _webcamOutput.Stopped += (_, e) => { if (!e.IsSuccess) layer.Failed = true; };
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to set up the webcam outputs: {ex.Message}");
                DisposeWebcamOutputs();
                return null;
            }

            return layer;
        }

        // The webcam is optional, so a failure here never stops the recording
        private static void StartWebcamOutputs(WebcamLayer? layer)
        {
            if (_webcamOutput != null && !_webcamOutput.Start())
            {
                Log.Warning($"Failed to start the webcam recording: {_webcamOutput.LastError ?? "Unknown error"}");
                if (layer != null)
                    layer.FilePath = string.Empty;
            }

            if (_webcamBufferOutput != null && !_webcamBufferOutput.Start())
                Log.Warning($"Failed to start the webcam replay buffer: {_webcamBufferOutput.LastError ?? "Unknown error"}");
        }

        private static void OnWebcamOutputStopped(object? sender, OutputStoppedEventArgs e)
        {
            if (!e.IsSuccess)
                Log.Warning($"OBS stopped a webcam output (code {e.Code}); last error: {e.LastError ?? "(none)"}");
        }

        // Requests the stop right away so it ends at the same moment as the main output; StopWebcamOutputs waits for it
        private static void BeginStopWebcamRecording()
        {
            try { _webcamOutput?.Stop(waitForCompletion: false); }
            catch (Exception ex) { Log.Warning($"Failed to stop the webcam recording: {ex.Message}"); }
        }

        private static void StopWebcamOutputs()
        {
            try
            {
                StopOutput(_webcamOutput, "webcam recording");
                StopOutput(_webcamBufferOutput, "webcam replay buffer");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to stop the webcam outputs: {ex.Message}");
            }
        }

        private static void ForceStopWebcamOutputs()
        {
            try
            {
                if (_webcamOutput?.IsActive == true) _webcamOutput.ForceStop();
                if (_webcamBufferOutput?.IsActive == true) _webcamBufferOutput.ForceStop();
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to force-stop the webcam outputs: {ex.Message}");
            }
        }

        // Call right after the main buffer's Save() so both replays end at the same moment
        private static Task<string?>? BeginWebcamReplaySave()
        {
            var buffer = _webcamBufferOutput;
            if (buffer == null || !buffer.IsActive)
                return null;

            try
            {
                return buffer.SaveAsync(GetReplaySaveExpectedTimeout());
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to save the webcam replay: {ex.Message}");
                return null;
            }
        }

        private static async Task<string?> EndWebcamReplaySave(Task<string?>? save)
        {
            if (save == null)
                return null;

            try
            {
                return await save;
            }
            catch (Exception ex)
            {
                Log.Warning($"Webcam replay save failed: {ex.Message}");
                return null;
            }
        }

        private static async Task ResetWebcamReplayBuffer()
        {
            var buffer = _webcamBufferOutput;
            if (buffer == null)
                return;

            if (!await buffer.ResetAsync(TimeSpan.FromSeconds(30)))
                Log.Warning($"Failed to restart the webcam replay buffer: {buffer.LastError ?? "Unknown error"}");
        }

        /// <summary>
        /// Pairs a finished webcam file with its video. Both outputs stop (or save) at the same
        /// moment, so their ends line up and the difference in length is the webcam's start offset.
        /// </summary>
        public static async Task<WebcamLayer?> FinishWebcamLayer(WebcamLayer? template, string? webcamPath, string videoPath)
        {
            if (string.IsNullOrEmpty(webcamPath))
                return null;
            // A session webcam that stopped early no longer ends with its video, so the offset can't be found
            if (template == null || (template.Failed && webcamPath == template.FilePath))
            {
                ContentService.DeleteWebcamFile(webcamPath);
                return null;
            }

            try
            {
                if (!File.Exists(webcamPath))
                    return null;

                await EnsureFileReady(webcamPath);
                TimeSpan webcamDuration = await FFmpegService.GetVideoDuration(webcamPath);
                TimeSpan videoDuration = await FFmpegService.GetVideoDuration(videoPath);
                if (webcamDuration == TimeSpan.Zero || videoDuration == TimeSpan.Zero)
                {
                    Log.Warning($"Could not read the length of {webcamPath} or its video; dropping the webcam layer");
                    ContentService.DeleteWebcamFile(webcamPath);
                    return null;
                }

                double offset = Math.Round((videoDuration - webcamDuration).TotalSeconds, 3);
                Log.Information($"Webcam layer {webcamPath} starts at {offset:F3}s");
                return new WebcamLayer { FilePath = webcamPath, Offset = offset, X = template.X, Y = template.Y, Size = template.Size };
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to finish the webcam layer: {ex.Message}");
                ContentService.DeleteWebcamFile(webcamPath);
                return null;
            }
        }

        private static void DisposeWebcamOutputs()
        {
            try { _webcamOutput?.Dispose(); } catch (Exception ex) { Log.Warning($"Error disposing webcam output: {ex.Message}"); }
            _webcamOutput = null;

            try { _webcamBufferOutput?.Dispose(); } catch (Exception ex) { Log.Warning($"Error disposing webcam buffer output: {ex.Message}"); }
            _webcamBufferOutput = null;
        }

        private static void DisposeWebcamSource()
        {
            DisposeWebcamOutputs();

            try { _webcamEncoder?.Dispose(); } catch (Exception ex) { Log.Warning($"Error disposing webcam encoder: {ex.Message}"); }
            _webcamEncoder = null;

            try
            {
                _webcamCanvas?.ClearChannel();
                _webcamItem?.Remove();
                _webcamItem?.Dispose();
                _webcamScene?.Remove();
                _webcamScene?.Dispose();
                _webcamSource?.Dispose();
                _webcamCanvas?.Remove();
                _webcamCanvas?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to dispose the webcam layer: {ex.Message}");
            }

            _webcamItem = null;
            _webcamScene = null;
            _webcamSource = null;
            _webcamCanvas = null;
        }
    }
}
