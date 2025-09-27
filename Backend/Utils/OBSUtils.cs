using LibObs;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Segra.Backend.Models;
using Segra.Backend.Services;
using Serilog;
using System.IO.Compression;
using System.Runtime.InteropServices;
using static LibObs.Obs;
using static Segra.Backend.Utils.GeneralUtils;
using size_t = System.UIntPtr;
using static Segra.Backend.Utils.MessageUtils;

namespace Segra.Backend.Utils
{
    public static class OBSUtils
    {
        private const uint OBS_SOURCE_FLAG_FORCE_MONO = 1u << 1; // from obs.h
        public static bool IsInitialized { get; private set; }
        public static GpuVendor DetectedGpuVendor { get; private set; } = DetectGpuVendor();
        static bool signalOutputStop = false;
        static IntPtr output = IntPtr.Zero;
        static IntPtr bufferOutput = IntPtr.Zero;
        static bool replaySaved = false;
        static IntPtr gameCaptureSource = IntPtr.Zero;
        static IntPtr displaySource = IntPtr.Zero;
        static List<IntPtr> micSources = new List<IntPtr>();
        static List<IntPtr> desktopSources = new List<IntPtr>();
        static IntPtr videoEncoder = IntPtr.Zero;
        static List<IntPtr> audioEncoders = new List<IntPtr>();
        private static string? hookedExecutableFileName;
        private static System.Threading.Timer? gameCaptureHookTimeoutTimer = null;
        static signal_callback_t outputStopCallback = (data, cd) =>
        {
            signalOutputStop = true;
        };
        static signal_callback_t replaySavedCallback = (data, cd) =>
        {
            replaySaved = true;
            Log.Information("Replay buffer saved callback received");
        };
        private static string? _lastReplayBufferPath;
        private static signal_callback_t? hookedCallback;
        private static signal_callback_t? unhookedCallback;
        private static bool _isGameCaptureHooked = false;

        internal static bool IsHdrCapableEncoder(string? encoderId)
        {
            if (string.IsNullOrWhiteSpace(encoderId))
            {
                return false;
            }

            string normalized = encoderId.ToLowerInvariant();
            return normalized.Contains("hevc") ||
                   normalized.Contains("h265") ||
                   normalized.Contains("av1");
        }

        private static string DetermineEncoderProfile(string encoderId, bool isHdr)
        {
            string normalized = encoderId.ToLowerInvariant();

            if (normalized.Contains("hevc") || normalized.Contains("h265"))
            {
                return isHdr ? "main10" : "main";
            }

            if (normalized.Contains("av1"))
            {
                return "main";
            }

            return "high";
        }

        public static bool SaveReplayBuffer()
        {
            // Check if replay buffer is active before trying to save
            if (bufferOutput == IntPtr.Zero || !obs_output_active(bufferOutput))
            {
                Log.Warning("Cannot save replay buffer: buffer is not active");
                return false;
            }

            Log.Information("Attempting to save replay buffer...");
            replaySaved = false;
            _lastReplayBufferPath = null;

            // Get the procedure handler for the replay buffer
            IntPtr procHandler = obs_output_get_proc_handler(bufferOutput);
            if (procHandler == IntPtr.Zero)
            {
                Log.Warning("Cannot save replay buffer: failed to get proc handler");
                return false;
            }

            // Step 1: Call the save procedure
            calldata_t cd = new calldata_t();
            IntPtr cdPtr = Marshal.AllocHGlobal(Marshal.SizeOf<calldata_t>());
            Marshal.StructureToPtr(cd, cdPtr, false);

            try
            {
                bool result = proc_handler_call(procHandler, "save", cd);

                if (!result)
                {
                    Log.Warning("Failed to save replay buffer");
                    return false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(cdPtr);
            }

            // Wait for the save callback to complete (up to 5 seconds)
            Log.Information("Waiting for replay buffer saved callback...");
            int attempts = 0;
            while (!replaySaved && attempts < 50)
            {
                Thread.Sleep(100);
                attempts++;
            }

            if (!replaySaved)
            {
                Log.Warning("Replay buffer may not have saved correctly");
                return false;
            }

            string? savedPath = _lastReplayBufferPath;
            if (string.IsNullOrEmpty(savedPath))
            {
                Thread.Sleep(1000);
                savedPath = _lastReplayBufferPath;
            }

            if (string.IsNullOrEmpty(savedPath))
            {
                Log.Error("Replay buffer path is null or empty");
                return false;
            }

            Log.Information($"Replay buffer saved to: {savedPath}");
            string game = Settings.Instance.State.Recording?.Game ?? "Unknown";

            // Create metadata for the buffer recording
            ContentUtils.CreateMetadataFile(savedPath, Content.ContentType.Buffer, game);
            ContentUtils.CreateThumbnail(savedPath, Content.ContentType.Buffer);
            Task.Run(() => ContentUtils.CreateWaveformFile(savedPath, Content.ContentType.Buffer));

            // Reload content list to include the new buffer file
            SettingsUtils.LoadContentFromFolderIntoState(true);

            Log.Information("Replay buffer save process completed successfully");

            // Reset the flag
            replaySaved = false;

            return true;
        }

        public static async Task InitializeAsync()
        {
            // Detect GPU vendor early in initialization
            DetectGpuVendor();

            if (IsInitialized)
                return;

            try
            {
                await CheckIfExistsOrDownloadAsync();
            }
            catch (Exception ex)
            {
                Log.Error($"OBS installation failed: {ex.Message}");
                await MessageUtils.ShowModal(
                    "Recorder Error",
                    "The recorder installation failed. Please check your internet connection and try again. If you have any games running, please close them and restart Segra.",
                    "error",
                    "Could not install recorder"
                );
                Settings.Instance.State.HasLoadedObs = true;
                return;
            }

            if (obs_initialized())
                throw new Exception("Error: OBS is already initialized.");

            base_set_log_handler(new log_handler_t((level, msg, args, p) =>
            {
                try
                {
                    string formattedMessage = MarshalUtils.GetLogMessage(msg, args);

                    if (formattedMessage.Contains("capture stopped"))
                        _isGameCaptureHooked = false;

                    if (formattedMessage.Contains("attempting to hook fullscreen process"))
                    {
                        if (Settings.Instance.State.PreRecording != null)
                        {
                            Settings.Instance.State.PreRecording.Status = "Waiting for game hook";

                            // If display recording is enabled, we don't need to show the "Waiting for game hook" message since it will start immediately.
                            if (Settings.Instance.EnableDisplayRecording == false)
                            {
                                _ = MessageUtils.SendSettingsToFrontend("Waiting for game hook");
                            }
                        }
                    }

                    // Check if this is a replay buffer save message
                    if (formattedMessage.Contains("Wrote replay buffer to"))
                    {
                        // Extract the path from the message
                        // Example: "[ffmpeg muxer: 'replay_buffer_output'] Wrote replay buffer to 'E:/Segra/buffers/2025-04-13_11-15-32.mp4'"
                        int lastQuoteIndex = formattedMessage.LastIndexOf("'");
                        int secondLastQuoteIndex = formattedMessage.LastIndexOf("'", lastQuoteIndex - 1);
                        int startIndex = secondLastQuoteIndex + 1;
                        int endIndex = lastQuoteIndex;

                        if (startIndex > 0 && endIndex > startIndex)
                        {
                            _lastReplayBufferPath = formattedMessage.Substring(startIndex, endIndex - startIndex);
                            Log.Information($"Extracted replay buffer path from log: {_lastReplayBufferPath}");
                        }
                    }

                    Log.Information($"{((LogErrorLevel)level)}: {formattedMessage}");
                }
                catch (Exception e)
                {
                    Log.Error(e.ToString());
                    if (e.StackTrace != null)
                    {
                        Log.Error(e.StackTrace);
                    }
                }
            }), IntPtr.Zero);

            Log.Information("libobs version: " + obs_get_version_string());

            // Step 1: Call obs_startup() as per documentation
            if (!obs_startup("en-US", null!, IntPtr.Zero))
                throw new Exception("Error during OBS startup.");

            // Step 2: Set modules path
            obs_add_data_path("./data/libobs/");
            obs_add_module_path("./obs-plugins/64bit/", "./data/obs-plugins/%module%/");

            // BUG: According to the documentation, ResetVideoSettings() should be called before loading modules but this causes black screen on recordings
            // https://github.com/Segergren/Segra/issues/1

            // Step 3: Reset audio settings as per documentation
            if (!ResetAudioSettings())
                throw new Exception("Failed to initialize audio settings.");

            // Step 4: Load modules
            obs_load_all_modules();
            obs_log_loaded_modules();

            // Step 5: Should be called before Step 4 as per documentation but this causes black screen on recordings
            // This probably causes the lag
            if (!ResetVideoSettings())
                throw new Exception("Failed to initialize video settings.");

            // Step 6: Post-load modules
            obs_post_load_modules();

            // Step 7: Set available encoders in state
            SetAvailableEncodersInState();

            IsInitialized = true;
            Settings.Instance.State.HasLoadedObs = true;
            Log.Information("OBS initialized successfully!");

            GameDetectionService.StartAsync();
        }

        private static bool ResetAudioSettings()
        {
            obs_audio_info audioInfo = new obs_audio_info()
            {
                samples_per_sec = 44100,
                speakers = speaker_layout.SPEAKERS_STEREO
            };

            return obs_reset_audio(ref audioInfo);
        }

        private static bool ResetVideoSettings(uint? customFps = null, uint? customOutputWidth = null, uint? customOutputHeight = null)
        {
            uint baseWidth, baseHeight;
            SettingsUtils.GetPrimaryMonitorResolution(out baseWidth, out baseHeight);

            // Use custom values if provided, otherwise use defaults
            uint outputWidth = customOutputWidth ?? baseWidth;
            uint outputHeight = customOutputHeight ?? baseHeight;

            // Check if the input aspect ratio is close to 4:3 (1.33)
            double aspectRatio = (double)baseWidth / baseHeight;
            bool is4by3 = Math.Abs(aspectRatio - 4.0 / 3.0) < 0.1; // Allow some tolerance

            // TODO: Implement a setting to disable this behavior
            // If the content is 4:3, stretch it to 16:9 while preserving height
            if (is4by3 && customOutputWidth == null)
            {
                // Calculate 16:9 width based on the current height
                outputWidth = (uint)(outputHeight * (16.0 / 9.0));
                Log.Information($"Stretching 4:3 content ({baseWidth}x{baseHeight}) to 16:9 ({outputWidth}x{outputHeight})");
            }

            obs_video_info videoInfo = new obs_video_info()
            {
                adapter = 0,
                graphics_module = "libobs-d3d11",
                fps_num = customFps ?? 60, // Default to 60 FPS if not specified
                fps_den = 1,
                base_width = baseWidth,
                base_height = baseHeight,
                output_width = outputWidth,
                output_height = outputHeight,
                output_format = video_format.VIDEO_FORMAT_NV12,
                gpu_conversion = true,
                colorspace = video_colorspace.VIDEO_CS_DEFAULT,
                range = video_range_type.VIDEO_RANGE_DEFAULT,
                scale_type = obs_scale_type.OBS_SCALE_BILINEAR
            };

            if (Settings.Instance.DynamicRange == DynamicRange.Hdr10)
            {
                videoInfo.output_format = video_format.VIDEO_FORMAT_P010;
                videoInfo.colorspace = video_colorspace.VIDEO_CS_2100_PQ;
                videoInfo.range = video_range_type.VIDEO_RANGE_FULL;
            }
            else
            {
                videoInfo.colorspace = video_colorspace.VIDEO_CS_709;
                videoInfo.range = video_range_type.VIDEO_RANGE_PARTIAL;
            }

            return obs_reset_video(ref videoInfo) == 0; // Returns true if successful
        }

        public static bool StartRecording(string name = "Manual Recording", string exePath = "Unknown", bool startManually = false)
        {
            Settings.Instance.State.PreRecording = new PreRecording { Game = name, Status = "Waiting to start" };
            bool isReplayBufferMode = Settings.Instance.RecordingMode == RecordingMode.Buffer;
            bool isSessionMode = Settings.Instance.RecordingMode == RecordingMode.Session;
            bool isHybridMode = Settings.Instance.RecordingMode == RecordingMode.Hybrid;
            bool isHdrRecording = Settings.Instance.DynamicRange == DynamicRange.Hdr10;

            string fileName = Path.GetFileName(exePath);

            var selectedCodec = Settings.Instance.Codec;
            if (selectedCodec == null)
            {
                Log.Error("No video encoder configured. Aborting recording start.");
                _ = Task.Run(() => ShowModal("Recording failed", "No video encoder is configured. Select an encoder in Settings and try again.", "error"));
                Settings.Instance.State.PreRecording = null;
                return false;
            }

            if (isHdrRecording && !IsHdrCapableEncoder(selectedCodec.InternalEncoderId))
            {
                Log.Error($"Selected encoder '{selectedCodec.InternalEncoderId}' does not support HDR recording.");
                _ = Task.Run(() => ShowModal("HDR codec required", "Select an HEVC or AV1 encoder to record in HDR.", "error"));
                Settings.Instance.State.PreRecording = null;
                return false;
            }

            // Prevent starting if any output is already active
            if (bufferOutput != IntPtr.Zero || output != IntPtr.Zero)
            {
                Log.Information("A recording or replay buffer is already in progress.");
                Settings.Instance.State.PreRecording = null;
                return false;
            }

            signalOutputStop = false;

            // Note: According to docs, audio settings cannot be reconfigured after initialization
            // but video can be reset as long as no outputs are active

            // Configure video settings specifically for this recording/buffer
            uint outputWidth, outputHeight;
            SettingsUtils.GetResolution(Settings.Instance.Resolution, out outputWidth, out outputHeight);

            if (!ResetVideoSettings(
                customFps: (uint)Settings.Instance.FrameRate,
                customOutputWidth: outputWidth,
                customOutputHeight: outputHeight))
            {
                throw new Exception("Failed to configure video settings for recording.");
            }

            _isGameCaptureHooked = false;

            IntPtr videoSourceSettings = obs_data_create();
            obs_data_set_string(videoSourceSettings, "capture_mode", "any_fullscreen");
            gameCaptureSource = obs_source_create("game_capture", "gameplay", videoSourceSettings, IntPtr.Zero);
            obs_data_release(videoSourceSettings);

            if (gameCaptureSource == IntPtr.Zero)
            {
                Log.Warning("Game Capture source not available. Falling back to Display Capture.");
                if (Settings.Instance.EnableDisplayRecording)
                {
                    AddMonitorCapture();
                    if (displaySource != IntPtr.Zero)
                    {
                        obs_set_output_source(0, displaySource);
                    }
                }
                else
                {
                    _ = Task.Run(() => ShowModal("Game Capture unavailable", "Game Capture plugin not found. Enable Display Recording in settings to proceed.", "warning"));
                }
            }
            else
            {
                obs_set_output_source(0, gameCaptureSource);

                // If display capture is enabled, start a timer to check if game capture hooks within 90 seconds
                if (Settings.Instance.EnableDisplayRecording)
                {
                    StartGameCaptureHookTimeoutTimer();
                }

                // Connect to 'hooked' and 'unhooked' signals for game capture
                IntPtr signalHandler = obs_source_get_signal_handler(gameCaptureSource);
                hookedCallback = new signal_callback_t(OnGameCaptureHooked);
                unhookedCallback = new signal_callback_t(OnGameCaptureUnhooked);
                signal_handler_connect(signalHandler, "hooked", hookedCallback, IntPtr.Zero);
                signal_handler_connect(signalHandler, "unhooked", unhookedCallback, IntPtr.Zero);
            }

            if (!startManually)
            {
                bool success = WaitForGameToStart();
                if (!success)
                {
                    Settings.Instance.State.Recording = null;
                    Settings.Instance.State.PreRecording = null;
                    _ = MessageUtils.SendSettingsToFrontend("Game did not start within the timeout period");
                    StopRecording();
                    return false;
                }
            }

            // Reset video settings to set correct output width for games with custom resolution
            Task.Delay(500).Wait();
            ResetVideoSettings(customFps: (uint)Settings.Instance.FrameRate);
            Task.Delay(1000).Wait();

            // If display recording is disabled, wait for game capture to hook
            if (!Settings.Instance.EnableDisplayRecording && gameCaptureSource != IntPtr.Zero)
            {
                bool hooked = WaitUntilGameCaptureHooks(startManually ? 90000 : 10000);
                if (!hooked)
                {
                    Settings.Instance.State.Recording = null;
                    Settings.Instance.State.PreRecording = null;
                    _ = MessageUtils.SendSettingsToFrontend("Game did not hook within the timeout period");
                    StopRecording();
                    return false;
                }
            }

            // Add monitor capture if enabled and game capture has not hooked yet
            if (Settings.Instance.EnableDisplayRecording && !_isGameCaptureHooked && gameCaptureSource != IntPtr.Zero)
            {
                AddMonitorCapture();
            }

            IntPtr videoEncoderSettings = obs_data_create();
            obs_data_set_string(videoEncoderSettings, "preset", "Quality");
            obs_data_set_bool(videoEncoderSettings, "use_bufsize", true);
            obs_data_set_string(videoEncoderSettings, "rate_control", Settings.Instance.RateControl);

            string encoderId = selectedCodec.InternalEncoderId;
            string encoderProfile = DetermineEncoderProfile(encoderId, isHdrRecording);
            obs_data_set_string(videoEncoderSettings, "profile", encoderProfile);

            if (isHdrRecording)
            {
                Log.Information($"Configured HDR pipeline with encoder profile '{encoderProfile}'.");
            }

            switch (Settings.Instance.RateControl)
            {
                case "CBR":
                    int targetBitrateKbps = Settings.Instance.Bitrate * 1000;
                    obs_data_set_int(videoEncoderSettings, "bitrate", (uint)targetBitrateKbps);
                    obs_data_set_int(videoEncoderSettings, "max_bitrate", (uint)targetBitrateKbps);
                    obs_data_set_int(videoEncoderSettings, "bufsize", (uint)targetBitrateKbps);
                    break;

                case "VBR":
                    int minBitrateKbps = Settings.Instance.MinBitrate * 1000;
                    int maxBitrateKbps = Settings.Instance.MaxBitrate * 1000;
                    obs_data_set_int(videoEncoderSettings, "bitrate", (uint)minBitrateKbps);
                    obs_data_set_int(videoEncoderSettings, "max_bitrate", (uint)maxBitrateKbps);
                    obs_data_set_int(videoEncoderSettings, "bufsize", (uint)maxBitrateKbps);
                    break;

                case "CRF":
                    // Software x264 path mainly; no explicit bitrate
                    obs_data_set_int(videoEncoderSettings, "crf", (uint)Settings.Instance.CrfValue);
                    break;

                case "CQP":
                    // Hardware encoders (NVENC/QSV/AMF) often use cqp/cq; provide both cqp and qp for compatibility
                    obs_data_set_int(videoEncoderSettings, "cqp", (uint)Settings.Instance.CqLevel);
                    obs_data_set_int(videoEncoderSettings, "qp", (uint)Settings.Instance.CqLevel);
                    break;

                default:
                    Settings.Instance.State.PreRecording = null;
                    throw new Exception("Unsupported Rate Control method.");
            }

            // Select the appropriate encoder based on settings and available hardware
            Log.Information($"Using encoder: {selectedCodec.FriendlyName} ({encoderId})");
            videoEncoder = obs_video_encoder_create(encoderId, "Segra Recorder", videoEncoderSettings, IntPtr.Zero);
            obs_encoder_set_video(videoEncoder, obs_get_video());
            obs_data_release(videoEncoderSettings);

            if (Settings.Instance.InputDevices != null && Settings.Instance.InputDevices.Count > 0)
            {
                int audioSourceIndex = 2;

                foreach (var deviceSetting in Settings.Instance.InputDevices)
                {
                    if (!string.IsNullOrEmpty(deviceSetting.Id))
                    {
                        IntPtr micSettings = obs_data_create();
                        obs_data_set_string(micSettings, "device_id", deviceSetting.Id);

                        string sourceName = $"Microphone_{micSources.Count + 1}";
                        IntPtr micSource = obs_source_create("wasapi_input_capture", sourceName, micSettings, IntPtr.Zero);

                        obs_data_release(micSettings);

                        // Apply Force Mono if enabled
                        SetForceMono(micSource, Settings.Instance.ForceMonoInputSources);

                        float volume = deviceSetting.Volume;
                        obs_source_set_volume(micSource, volume);

                        obs_set_output_source((uint)audioSourceIndex, micSource);
                        micSources.Add(micSource);

                        audioSourceIndex++;
                        Log.Information($"Added input device: {deviceSetting.Id} as {sourceName} with volume {volume}");
                    }
                }
            }

            if (Settings.Instance.OutputDevices != null && Settings.Instance.OutputDevices.Count > 0)
            {
                int desktopSourceIndex = micSources.Count + 2;

                foreach (var deviceSetting in Settings.Instance.OutputDevices)
                {
                    if (!string.IsNullOrEmpty(deviceSetting.Id))
                    {
                        IntPtr desktopSettings = obs_data_create();
                        obs_data_set_string(desktopSettings, "device_id", deviceSetting.Id);

                        string sourceName = $"DesktopAudio_{desktopSources.Count + 1}";
                        IntPtr desktopSource = obs_source_create("wasapi_output_capture", sourceName, desktopSettings, IntPtr.Zero);

                        obs_data_release(desktopSettings);

                        float desktopVolume = 1.0f; // Use fixed volume (100%)
                        obs_source_set_volume(desktopSource, desktopVolume);

                        obs_set_output_source((uint)desktopSourceIndex, desktopSource);
                        desktopSources.Add(desktopSource);

                        desktopSourceIndex++;
                        Log.Information($"Added output device: {deviceSetting.Name} ({deviceSetting.Id}) as {sourceName} with fixed volume {desktopVolume}");
                    }
                }
            }

            // Configure mixers and audio encoders based on setting.
            // If enabled: Track 1 = Full Mix, Tracks 2..6 = per-source isolated (up to 5 sources)
            // If disabled: Track 1 only (Full Mix)
            var allAudioSources = new List<IntPtr>();
            allAudioSources.AddRange(micSources);
            allAudioSources.AddRange(desktopSources);

            bool separateTracks = Settings.Instance.EnableSeparateAudioTracks;
            int maxTracks = 6; // OBS supports up to 6 audio tracks
            int perSourceTracks = separateTracks ? Math.Min(allAudioSources.Count, maxTracks - 1) : 0; // tracks 2..6 for sources
            int trackCount = 1 + perSourceTracks; // Track 1 is always the full mix

            for (int i = 0; i < allAudioSources.Count; i++)
            {
                try
                {
                    // Always include Track 1 (bit 0) as a full mix
                    uint mixersMask = 1u << 0;

                    // If enabled, give first 5 sources their own isolated tracks on 2..6 (bits 1..5)
                    if (separateTracks && i < (maxTracks - 1))
                    {
                        mixersMask |= (uint)(1 << (i + 1));
                    }
                    else
                    {
                        if (separateTracks && i >= (maxTracks - 1))
                            Log.Warning($"Audio source index {i} exceeds {maxTracks - 1} dedicated per-source tracks. It will be available in the master mix (Track 1) only.");
                    }
                    obs_source_set_audio_mixers(allAudioSources[i], mixersMask);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to set mixers for audio source {i}: {ex.Message}");
                }
            }

            // Create one audio encoder per track and bind to corresponding mixer index
            audioEncoders.Clear();
            for (int t = 0; t < trackCount; t++)
            {
                IntPtr audioEncoderSettings = obs_data_create();
                obs_data_set_int(audioEncoderSettings, "bitrate", 128);
                string encoderName = $"simple_aac_encoder_{t + 1}";
                IntPtr enc = obs_audio_encoder_create("ffmpeg_aac", encoderName, audioEncoderSettings, (UIntPtr)(uint)t, IntPtr.Zero);
                obs_data_release(audioEncoderSettings);
                obs_encoder_set_audio(enc, obs_get_audio());
                audioEncoders.Add(enc);
            }
            

            // Paths for session recordings and buffer
            string sessionDir = Settings.Instance.ContentFolder + "/sessions";
            string bufferDir = Settings.Instance.ContentFolder + "/buffers";
            if (!Directory.Exists(sessionDir)) Directory.CreateDirectory(sessionDir);
            if (!Directory.Exists(bufferDir)) Directory.CreateDirectory(bufferDir);

            string? videoOutputPath = null; // only set for session/hybrid session output

            // Configure outputs depending on mode
            if (isReplayBufferMode || isHybridMode)
            {
                IntPtr bufferOutputSettings = obs_data_create();
                obs_data_set_string(bufferOutputSettings, "directory", bufferDir);
                obs_data_set_string(bufferOutputSettings, "format", "%CCYY-%MM-%DD_%hh-%mm-%ss");
                obs_data_set_string(bufferOutputSettings, "extension", "mp4");
                obs_data_set_int(bufferOutputSettings, "max_time_sec", (uint)Settings.Instance.ReplayBufferDuration);
                obs_data_set_int(bufferOutputSettings, "max_size_mb", (uint)Settings.Instance.ReplayBufferMaxSize);
                uint bufferTracksMask = trackCount == 0 ? 0u : (1u << trackCount) - 1u;
                obs_data_set_int(bufferOutputSettings, "tracks", bufferTracksMask);

                bufferOutput = obs_output_create("replay_buffer", "replay_buffer_output", bufferOutputSettings, IntPtr.Zero);
                obs_data_release(bufferOutputSettings);

                obs_output_set_video_encoder(bufferOutput, videoEncoder);
                for (int t = 0; t < audioEncoders.Count; t++)
                {
                    obs_output_set_audio_encoder(bufferOutput, audioEncoders[t], (uint)t);
                }

                IntPtr bufferOutputHandler = obs_output_get_signal_handler(bufferOutput);
                signal_handler_connect(bufferOutputHandler, "stop", outputStopCallback, IntPtr.Zero);
                signal_handler_connect(bufferOutputHandler, "saved", replaySavedCallback, IntPtr.Zero);
            }

            if (isSessionMode || isHybridMode)
            {
                videoOutputPath = $"{sessionDir}/{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";

                IntPtr outputSettings = obs_data_create();
                obs_data_set_string(outputSettings, "path", videoOutputPath);
                obs_data_set_string(outputSettings, "format_name", "mp4");
                uint recordTracksMask = trackCount == 0 ? 0u : (1u << trackCount) - 1u;
                obs_data_set_int(outputSettings, "tracks", recordTracksMask);

                output = obs_output_create("ffmpeg_muxer", "simple_output", outputSettings, IntPtr.Zero);
                obs_data_release(outputSettings);

                obs_output_set_video_encoder(output, videoEncoder);
                for (int t = 0; t < audioEncoders.Count; t++)
                {
                    obs_output_set_audio_encoder(output, audioEncoders[t], (uint)t);
                }

                signal_handler_connect(obs_output_get_signal_handler(output), "stop", outputStopCallback, IntPtr.Zero);
            }

            // Overwrite the file name with the hooked executable name if using game hook
            fileName = hookedExecutableFileName ?? fileName;

            _ = Task.Run(() => PlaySound("start", 50));

            if (output != IntPtr.Zero)
            {
                if (!obs_output_start(output))
                {
                    string error = obs_output_get_last_error(output);
                    Log.Error($"Failed to start recording: {error}");
                    Task.Run(() => ShowModal("Recording failed", "Failed to start recording. Check the log for more details.", "error"));
                    Task.Run(() => PlaySound("error", 500));
                    Settings.Instance.State.PreRecording = null;
                    StopRecording();
                    return false;
                }

                Log.Information("Session recording started successfully");
            }

            if (bufferOutput != IntPtr.Zero)
            {
                if (!obs_output_start(bufferOutput))
                {
                    string error = obs_output_get_last_error(bufferOutput);
                    Log.Error($"Failed to start replay buffer: {error}");
                    Task.Run(() => ShowModal("Replay buffer failed", "Failed to start replay buffer. Check the log for more details.", "error"));
                    Task.Run(() => PlaySound("error", 500));
                    Settings.Instance.State.PreRecording = null;
                    StopRecording();
                    return false;
                }

                Log.Information("Replay buffer started successfully");
            }

            string? gameImage = GameIconUtils.ExtractIconAsBase64(exePath);

            Settings.Instance.State.Recording = new Recording()
            {
                StartTime = DateTime.Now,
                Game = name,
                FilePath = videoOutputPath,
                FileName = fileName,
                IsUsingGameHook = _isGameCaptureHooked,
                GameImage = gameImage
            };
            Settings.Instance.State.PreRecording = null;
            _ = MessageUtils.SendSettingsToFrontend("OBS Start recording");

            Log.Information("Recording started: " + videoOutputPath);
            if (!isReplayBufferMode)
            {
                _ = GameIntegrationService.Start(name);
            }
            Task.Run(KeybindCaptureService.Start);
            return true;
        }

        public static void AddMonitorCapture()
        {
            IntPtr displayCaptureSettings = obs_data_create();

            if (Settings.Instance.SelectedDisplay != null)
            {
                int? monitorIndex = Settings.Instance.State.Displays
                    .Select((d, i) => new { Display = d, Index = i })
                    .Where(x => x.Display.DeviceId == Settings.Instance.SelectedDisplay?.DeviceId)
                    .Select(x => (int?)x.Index)
                    .FirstOrDefault();

                if (monitorIndex.HasValue)
                {
                    obs_data_set_int(displayCaptureSettings, "monitor", (uint)monitorIndex.Value);
                }
                else
                {
                    _ = MessageUtils.ShowModal("Display recording", $"Could not find selected display. Defaulting to first automatically detected display.", "warning");
                }
            }
            displaySource = obs_source_create("monitor_capture", "display", displayCaptureSettings, IntPtr.Zero);
            obs_data_release(displayCaptureSettings);
            obs_set_output_source(1, displaySource);
        }

        public static void StopRecording()
        {
            bool isReplayBufferMode = Settings.Instance.RecordingMode == RecordingMode.Buffer;
            bool isHybridMode = Settings.Instance.RecordingMode == RecordingMode.Hybrid;

            if (isReplayBufferMode && bufferOutput != IntPtr.Zero)
            {
                // Stop replay buffer
                signalOutputStop = false;
                obs_output_stop(bufferOutput);

                int attempts = 0;
                while (!signalOutputStop && attempts < 300)
                {
                    Thread.Sleep(100);
                    attempts++;
                }

                if (!signalOutputStop)
                {
                    Log.Warning("Failed to stop replay buffer. Forcing stop.");
                    obs_output_force_stop(bufferOutput);
                }
                else
                {
                    Log.Information("Replay buffer stopped.");
                }

                Thread.Sleep(200);

                DisposeOutput();
                DisposeSources();
                DisposeEncoders();

                Log.Information("Replay buffer stopped and disposed.");

                _ = GameIntegrationService.Shutdown();
                KeybindCaptureService.Stop();

                // Reload content list
                SettingsUtils.LoadContentFromFolderIntoState(false);
            }
            else if (!isReplayBufferMode && !isHybridMode && output != IntPtr.Zero)
            {
                // Stop standard recording
                if (Settings.Instance.State.Recording != null)
                    Settings.Instance.State.UpdateRecordingEndTime(DateTime.Now);

                signalOutputStop = false;
                obs_output_stop(output);

                int attempts = 0;
                while (!signalOutputStop && attempts < 300)
                {
                    Thread.Sleep(100);
                    attempts++;
                }

                if (!signalOutputStop)
                {
                    Log.Warning("Failed to stop recording. Forcing stop.");
                    obs_output_force_stop(output);
                }
                else
                {
                    Log.Information("Output stopped.");
                }

                Thread.Sleep(200);

                DisposeOutput();
                DisposeSources();
                DisposeEncoders();

                output = IntPtr.Zero;

                Log.Information("Recording stopped.");

                _ = GameIntegrationService.Shutdown();
                KeybindCaptureService.Stop();

                // Might be null or empty if the recording failed to start
                if (Settings.Instance.State.Recording != null && Settings.Instance.State.Recording.FilePath != null)
                {
                    ContentUtils.CreateMetadataFile(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session, Settings.Instance.State.Recording.Game, Settings.Instance.State.Recording.Bookmarks);
                    ContentUtils.CreateThumbnail(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session);
                    Task.Run(() => ContentUtils.CreateWaveformFile(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session));

                    Log.Information($"Recording details:");
                    Log.Information($"Start Time: {Settings.Instance.State.Recording.StartTime}");
                    Log.Information($"End Time: {Settings.Instance.State.Recording.EndTime}");
                    Log.Information($"Duration: {Settings.Instance.State.Recording.Duration}");
                    Log.Information($"File Path: {Settings.Instance.State.Recording.FilePath}");
                }

                SettingsUtils.LoadContentFromFolderIntoState(false);
            }
            else if (isHybridMode)
            {
                if (Settings.Instance.State.Recording != null)
                    Settings.Instance.State.UpdateRecordingEndTime(DateTime.Now);

                // Stop replay buffer first if running
                if (bufferOutput != IntPtr.Zero)
                {
                    signalOutputStop = false;
                    obs_output_stop(bufferOutput);
                    int attempts = 0;
                    while (!signalOutputStop && attempts < 300)
                    {
                        Thread.Sleep(100);
                        attempts++;
                    }
                    if (!signalOutputStop)
                    {
                        Log.Warning("Hybrid: Failed to stop replay buffer. Forcing stop.");
                        obs_output_force_stop(bufferOutput);
                    }
                    else
                    {
                        Log.Information("Hybrid: Replay buffer stopped.");
                    }
                }

                // Stop session recording
                if (output != IntPtr.Zero)
                {
                    signalOutputStop = false;
                    obs_output_stop(output);
                    int attempts2 = 0;
                    while (!signalOutputStop && attempts2 < 300)
                    {
                        Thread.Sleep(100);
                        attempts2++;
                    }
                    if (!signalOutputStop)
                    {
                        Log.Warning("Hybrid: Failed to stop recording. Forcing stop.");
                        obs_output_force_stop(output);
                    }
                    else
                    {
                        Log.Information("Hybrid: Recording stopped.");
                    }
                }

                Thread.Sleep(200);

                DisposeOutput();
                DisposeSources();
                DisposeEncoders();

                _ = GameIntegrationService.Shutdown();
                KeybindCaptureService.Stop();

                if (Settings.Instance.State.Recording != null && Settings.Instance.State.Recording.FilePath != null)
                {
                    ContentUtils.CreateMetadataFile(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session, Settings.Instance.State.Recording.Game, Settings.Instance.State.Recording.Bookmarks);
                    ContentUtils.CreateThumbnail(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session);
                    Task.Run(() => ContentUtils.CreateWaveformFile(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session));
                }

                SettingsUtils.LoadContentFromFolderIntoState(false);
            }
            else
            {
                DisposeOutput();
                DisposeSources();
                DisposeEncoders();
                Settings.Instance.State.Recording = null;
                Settings.Instance.State.PreRecording = null;
            }

            Task.Run(StorageUtils.EnsureStorageBelowLimit);

            // Reset hooked executable file name
            hookedExecutableFileName = null;

            // If the recording ends before it started, don't do anything
            if (Settings.Instance.State.Recording == null || (!isReplayBufferMode && Settings.Instance.State.Recording.FilePath == null))
            {
                return;
            }

            // Get the file path before nullifying the recording (FilePath is not null at this point because of the previous check)
            string filePath = Settings.Instance.State.Recording.FilePath!;

            // Reset the recording and pre-recording
            Settings.Instance.State.Recording = null;
            Settings.Instance.State.PreRecording = null;

            // If the recording is not a replay buffer recording, AI is enabled, user is authenticated, and auto generate highlights is enabled -> analyze the video!
            if (Settings.Instance.EnableAi && AuthService.IsAuthenticated() && Settings.Instance.AutoGenerateHighlights && !isReplayBufferMode)
            {
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                Task.Run(() => AiService.AnalyzeVideo(fileName));
            }
        }

        [System.Diagnostics.DebuggerStepThrough]
        private static void OnGameCaptureHooked(IntPtr data, calldata_t cd)
        {
            IntPtr cdPtr = Marshal.AllocHGlobal(Marshal.SizeOf<calldata_t>());
            Marshal.StructureToPtr(cd, cdPtr, false);

            if (cdPtr == IntPtr.Zero)
            {
                Log.Warning("GameCaptureHooked callback received null calldata pointer.");
                return;
            }

            try
            {
                calldata_get_string(cdPtr, "title", out IntPtr title);
                calldata_get_string(cdPtr, "class", out IntPtr windowClass);
                calldata_get_string(cdPtr, "executable", out IntPtr executable);

                _isGameCaptureHooked = true;
                StopGameCaptureHookTimeoutTimer();
                DisposeDisplaySource();
                Log.Information($"Game hooked: Title='{Marshal.PtrToStringAnsi(title)}', Class='{Marshal.PtrToStringAnsi(windowClass)}', Executable='{Marshal.PtrToStringAnsi(executable)}'");

                // Overwrite the file name with the hooked one because sometimes the current tracked file name is the startup exe instead of the actual game 
                hookedExecutableFileName = Marshal.PtrToStringAnsi(executable);
                if (Settings.Instance.State.Recording != null)
                {
                    if (hookedExecutableFileName != null)
                    {
                        Settings.Instance.State.Recording.FileName = hookedExecutableFileName;
                    }
                    Settings.Instance.State.Recording.IsUsingGameHook = true;
                    _ = MessageUtils.SendSettingsToFrontend("Updated game hook");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing OnGameCaptureHooked signal");
            }
        }

        private static void OnGameCaptureUnhooked(IntPtr data, calldata_t cd)
        {
            IntPtr cdPtr = Marshal.AllocHGlobal(Marshal.SizeOf<calldata_t>());
            Marshal.StructureToPtr(cd, cdPtr, false);

            _isGameCaptureHooked = false;
            Log.Information("Game unhooked.");
        }

        private static void SetForceMono(IntPtr source, bool forceMono)
        {
            if (source == IntPtr.Zero) return;
            try
            {
                uint flags = obs_source_get_flags(source);
                bool currentlyMono = (flags & OBS_SOURCE_FLAG_FORCE_MONO) != 0;
                if (forceMono && !currentlyMono)
                {
                    obs_source_set_flags(source, flags | OBS_SOURCE_FLAG_FORCE_MONO);
                }
                else if (!forceMono && currentlyMono)
                {
                    obs_source_set_flags(source, flags & ~OBS_SOURCE_FLAG_FORCE_MONO);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to set force mono on source: {ex.Message}");
            }
        }

        private static bool WaitForGameToStart(int timeoutMs = 80000)
        {
            int elapsed = 0;
            const int step = 100;
            Log.Information("Waiting for trying to hook game...");
            Log.Information("PreRecording Status: {PreRecordingStatus}", Settings.Instance.State.PreRecording?.Status);
            while (Settings.Instance.State.PreRecording?.Status != "Waiting for game hook")
            {
                Thread.Sleep(step);
                elapsed += step;
                Log.Information("PreRecording Status: {PreRecordingStatus}", Settings.Instance.State.PreRecording?.Status);
                if (elapsed >= timeoutMs)
                {
                    Log.Warning("Game Capture did not hook within {Seconds} seconds.", timeoutMs / 1000);
                    return false;
                }
            }

            return true;
        }

        private static bool WaitUntilGameCaptureHooks(int timeoutMs = 10000)
        {
            int elapsed = 0;
            const int step = 100;

            while (!_isGameCaptureHooked)
            {
                Thread.Sleep(step);
                elapsed += step;
                if (elapsed >= timeoutMs)
                {
                    Log.Warning("Game Capture did not hook within {Seconds} seconds. Preventing retry until foreground window changes", timeoutMs / 1000);
                    GameDetectionService.PreventRetryRecording = true;
                    return false;
                }
            }

            return true;
        }

        public static void DisposeSources()
        {
            DisposeDisplaySource();
            DisposeGameCaptureSource();

            int micSourcesCount = micSources.Count;
            for (int i = 0; i < micSources.Count; i++)
            {
                if (micSources[i] != IntPtr.Zero)
                {
                    obs_set_output_source((uint)(i + 2), IntPtr.Zero);
                    obs_source_release(micSources[i]);
                    micSources[i] = IntPtr.Zero;
                }
            }
            micSources.Clear();

            for (int i = 0; i < desktopSources.Count; i++)
            {
                if (desktopSources[i] != IntPtr.Zero)
                {
                    int desktopIndex = i + micSourcesCount + 2;
                    obs_set_output_source((uint)desktopIndex, IntPtr.Zero);
                    obs_source_release(desktopSources[i]);
                    desktopSources[i] = IntPtr.Zero;
                }
            }
            desktopSources.Clear();
        }

        public static void DisposeGameCaptureSource()
        {
            if (gameCaptureSource != IntPtr.Zero)
            {
                try
                {
                    // Disconnect signal handlers before releasing the source
                    IntPtr handler = obs_source_get_signal_handler(gameCaptureSource);
                    if (hookedCallback != null)
                        signal_handler_disconnect(handler, "hooked", hookedCallback, IntPtr.Zero);
                    if (unhookedCallback != null)
                        signal_handler_disconnect(handler, "unhooked", unhookedCallback, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to disconnect game capture signals: {ex.Message}");
                }
                obs_set_output_source(0, IntPtr.Zero);
                obs_source_release(gameCaptureSource);
                gameCaptureSource = IntPtr.Zero;
            }
            // Dispose the timer if it exists
            StopGameCaptureHookTimeoutTimer();
        }

        private static void StartGameCaptureHookTimeoutTimer()
        {
            // Dispose any existing timer first
            StopGameCaptureHookTimeoutTimer();

            // Create a new timer that checks after 90 seconds
            gameCaptureHookTimeoutTimer = new System.Threading.Timer(
                CheckGameCaptureHookStatus,
                null,
                90000, // 90 seconds delay
                Timeout.Infinite // Don't repeat
            );

            Log.Information("Started game capture hook timer (90 seconds)");
        }

        private static void StopGameCaptureHookTimeoutTimer()
        {
            if (gameCaptureHookTimeoutTimer != null)
            {
                gameCaptureHookTimeoutTimer.Dispose();
                gameCaptureHookTimeoutTimer = null;
                Log.Information("Stopped game capture hook timer");
            }
        }

        private static void CheckGameCaptureHookStatus(object? state)
        {
            // Check if game capture has hooked
            if (!_isGameCaptureHooked && Settings.Instance.EnableDisplayRecording)
            {
                Log.Warning("Game capture did not hook within 90 seconds. Removing game capture source.");
                DisposeGameCaptureSource();
            }
            else
            {
                Log.Information("Game capture hook check completed. Hook status: {0}", _isGameCaptureHooked ? "Hooked" : "Not hooked");
                // Just stop the timer without disposing the game capture source if it's hooked
                StopGameCaptureHookTimeoutTimer();
            }
        }

        public static void DisposeDisplaySource()
        {
            if (displaySource != IntPtr.Zero)
            {
                obs_set_output_source(1, IntPtr.Zero);
                obs_source_release(displaySource);
                displaySource = IntPtr.Zero;
            }
        }

        public static void DisposeEncoders()
        {
            if (videoEncoder != IntPtr.Zero)
            {
                obs_encoder_release(videoEncoder);
                videoEncoder = IntPtr.Zero;
            }

            if (audioEncoders.Count > 0)
            {
                foreach (var enc in audioEncoders)
                {
                    if (enc != IntPtr.Zero)
                        obs_encoder_release(enc);
                }
                audioEncoders.Clear();
            }
        }

        public static void DisposeOutput()
        {
            if (output != IntPtr.Zero)
            {
                signal_handler_disconnect(obs_output_get_signal_handler(output), "stop", outputStopCallback, IntPtr.Zero);
                obs_output_release(output);
                output = IntPtr.Zero;
            }

            if (bufferOutput != IntPtr.Zero)
            {
                if (replaySavedCallback != null)
                {
                    signal_handler_disconnect(obs_output_get_signal_handler(bufferOutput), "saved", replaySavedCallback, IntPtr.Zero);
                }
                signal_handler_disconnect(obs_output_get_signal_handler(bufferOutput), "stop", outputStopCallback, IntPtr.Zero);
                obs_output_release(bufferOutput);
                bufferOutput = IntPtr.Zero;
            }
        }

        private static async Task CheckIfExistsOrDownloadAsync()
        {
            Log.Information("Checking if OBS is installed");

            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string dllPath = Path.Combine(currentDirectory, "obs.dll");

            if (File.Exists(dllPath))
            {
                Log.Information("OBS is installed");
                return;
            }

            // Store obs.zip and hash in AppData to preserve them across updates
            string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");
            Directory.CreateDirectory(appDataDir); // Ensure directory exists

            string zipPath = Path.Combine(appDataDir, "obs.zip");
            string apiUrl = "https://api.github.com/repos/Segergren/Segra/contents/obs.zip?ref=main";
            string localHashPath = Path.Combine(appDataDir, "obs.hash");
            bool needsDownload = true;

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Segra");
                httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3.json");

                Log.Information("Fetching file metadata...");

                var response = await httpClient.GetAsync(apiUrl);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Error($"Failed to fetch metadata from {apiUrl}. Status: {response.StatusCode}");
                    throw new Exception($"Failed to fetch file metadata: {response.ReasonPhrase}");
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var metadata = System.Text.Json.JsonSerializer.Deserialize<GitHubFileMetadata>(jsonResponse);

                if (metadata?.DownloadUrl == null)
                {
                    Log.Error("Download URL not found in the API response.");
                    throw new Exception("Invalid API response: Missing download URL.");
                }

                string remoteHash = metadata.Sha;

                // Check if we already have the file with the correct hash
                if (File.Exists(zipPath) && File.Exists(localHashPath))
                {
                    string localHash = await File.ReadAllTextAsync(localHashPath);
                    if (localHash == remoteHash)
                    {
                        Log.Information("Found existing obs.zip with matching hash. Skipping download.");
                        needsDownload = false;
                    }
                    else
                    {
                        Log.Information("Found existing obs.zip but hash doesn't match. Downloading new version.");
                    }
                }

                if (needsDownload)
                {
                    Log.Information("Downloading OBS...");

                    httpClient.DefaultRequestHeaders.Clear();
                    var zipBytes = await httpClient.GetByteArrayAsync(metadata.DownloadUrl);
                    await File.WriteAllBytesAsync(zipPath, zipBytes);

                    // Save the hash for future reference
                    await File.WriteAllTextAsync(localHashPath, remoteHash);

                    Log.Information("Download complete");
                }
            }

            Log.Information("Extracting OBS...");
            ZipFile.ExtractToDirectory(zipPath, currentDirectory, true);
            Log.Information("OBS setup complete");
        }

        private class GitHubFileMetadata
        {
            [System.Text.Json.Serialization.JsonPropertyName("sha")]
            public required string Sha { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("download_url")]
            public required string DownloadUrl { get; set; }
        }

        public static void PlaySound(string resourceName, int delay = 0)
        {
            Thread.Sleep(delay);
            using var stream = Properties.Resources.ResourceManager.GetStream(resourceName);
            if (stream == null)
                throw new ArgumentException($"Resource '{resourceName}' not found or not a stream.");

            using var reader = new WaveFileReader(stream);
            var sampleProvider = reader.ToSampleProvider();
            var volumeProvider = new VolumeSampleProvider(sampleProvider)
            {
                Volume = Settings.Instance.SoundEffectsVolume
            };

            using var waveOut = new WaveOutEvent();
            waveOut.Init(volumeProvider);
            waveOut.Play();

            while (waveOut.PlaybackState == PlaybackState.Playing)
                Thread.Sleep(50);
        }

        private static readonly Dictionary<string, string> EncoderFriendlyNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // ── NVIDIA NVENC ────────────────────────────────────
                ["jim_nvenc"] = "NVIDIA NVENC H.264",
                ["jim_hevc_nvenc"] = "NVIDIA NVENC H.265",
                ["jim_av1_nvenc"] = "NVIDIA NVENC AV1",

                // ── AMD AMF ────────────────────────────────────────
                ["h264_texture_amf"] = "AMD AMF H.264",
                ["h265_texture_amf"] = "AMD AMF H.265",
                ["av1_texture_amf"] = "AMD AMF AV1",

                // ── Intel Quick Sync ───────────────────────────────
                ["obs_qsv11_v2"] = "Intel QSV H.264",
                ["obs_qsv11_hevc"] = "Intel QSV H.265",
                ["obs_qsv11_av1"] = "Intel QSV AV1",

                // ── CPU / software paths ───────────────────────────
                ["obs_x264"] = "Software x264",
                ["ffmpeg_svt_av1"] = "Software SVT-AV1",
                ["ffmpeg_aom_av1"] = "Software AOM AV1",
                ["ffmpeg_openh264"] = "Software OpenH264",
            };

        private static void SetAvailableEncodersInState()
        {
            Log.Information("Available encoders:");

            // Enumerate all encoder types
            string encoderId = string.Empty;
            size_t idx = 0;

            while (obs_enum_encoder_types(idx, ref encoderId))
            {
                EncoderFriendlyNames.TryGetValue(encoderId, out var name);
                string friendlyName = name ?? encoderId;
                bool isHardware = encoderId.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
                                  encoderId.Contains("amf", StringComparison.OrdinalIgnoreCase) ||
                                  encoderId.Contains("qsv", StringComparison.OrdinalIgnoreCase);

                Log.Information($"{idx} - {friendlyName} | {encoderId} | {(isHardware ? "Hardware" : "Software")}");
                if (name != null)
                {
                    Settings.Instance.State.Codecs.Add(new Codec { InternalEncoderId = encoderId, FriendlyName = friendlyName, IsHardwareEncoder = isHardware });
                }
                idx++;
            }

            Log.Information($"Total encoders found: {idx}");

            if (Settings.Instance.Codec == null)
            {
                Settings.Instance.Codec = SelectDefaultCodec(Settings.Instance.Encoder, Settings.Instance.State.Codecs);
            }
        }

        public static Codec? SelectDefaultCodec(string encoderType, List<Codec> availableCodecs)
        {
            if (availableCodecs == null || availableCodecs.Count == 0)
            {
                return null;
            }

            Codec? selectedCodec = null;

            if (encoderType == "cpu")
            {
                // Prefer obs_x264 if available
                selectedCodec = availableCodecs.FirstOrDefault(
                    c => c.InternalEncoderId.Equals(
                        "obs_x264",
                        StringComparison.OrdinalIgnoreCase
                    )
                );

                // If not found, fallback to first software (CPU) encoder
                if (selectedCodec == null)
                {
                    selectedCodec = availableCodecs.FirstOrDefault(
                        c => !c.IsHardwareEncoder
                    );
                }
            }
            else if (encoderType == "gpu")
            {
                // Prefer NVIDIA NVENC (jim_nvenc)
                selectedCodec = availableCodecs.FirstOrDefault(
                    c => c.InternalEncoderId.Equals(
                        "jim_nvenc",
                        StringComparison.OrdinalIgnoreCase
                    )
                );

                // If not found, try AMD AMF H.264
                if (selectedCodec == null)
                {
                    selectedCodec = availableCodecs.FirstOrDefault(
                        c => c.InternalEncoderId.Equals(
                            "h264_texture_amf",
                            StringComparison.OrdinalIgnoreCase
                        )
                    );
                }

                // If still not found, fallback to first hardware encoder
                if (selectedCodec == null)
                {
                    selectedCodec = availableCodecs.FirstOrDefault(
                        c => c.IsHardwareEncoder
                    );
                }
            }

            // Ultimate fallback: First available encoder if no match or no selection
            if (selectedCodec == null)
            {
                selectedCodec = availableCodecs.FirstOrDefault();
            }

            return selectedCodec;
        }
    }
}
