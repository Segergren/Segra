using LibObs;
using NAudio.Wave;
using Segra.Backend.Models;
using Segra.Backend.Services;
using Serilog;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows.Forms.Design;
using static LibObs.Obs;
using size_t = System.UIntPtr;

namespace Segra.Backend.Utils
{
    public static class OBSUtils
    {
        public static bool isInitializingRecording = false;
        // Enum for GPU vendor types
        public enum GpuVendor
        {
            Unknown,
            Nvidia,
            AMD,
            Intel
        }

        public static bool IsInitialized { get; private set; }
        public static GpuVendor DetectedGpuVendor { get; private set; } = GpuVendor.Unknown;
        static bool signalOutputStop = false;
        static IntPtr output = IntPtr.Zero;
        static IntPtr bufferOutput = IntPtr.Zero;
        static bool replaySaved = false;
        static IntPtr displaySource = IntPtr.Zero;
        static List<IntPtr> micSources = new List<IntPtr>();
        static List<IntPtr> desktopSources = new List<IntPtr>();
        static IntPtr videoEncoder = IntPtr.Zero;
        static IntPtr audioEncoder = IntPtr.Zero;
        private static string? hookedExecutableFileName;

        // Available encoder IDs for different hardware
        private const string NVIDIA_ENCODER = "jim_nvenc";
        private const string AMD_ENCODER = "h264_texture_amf";
        private const string INTEL_ENCODER = "qsv_h264";
        private const string CPU_ENCODER = "x264";

        static signal_callback_t outputStopCallback = (data, cd) =>
        {
            signalOutputStop = true;
        };

        static signal_callback_t replaySavedCallback = (data, cd) =>
        {
            replaySaved = true;
            Log.Information("Replay buffer saved callback received");
        };

        // Variable to store the replay buffer path extracted from logs
        private static string? _lastReplayBufferPath;

        static signal_callback_t hookedCallback;
        static signal_callback_t unhookedCallback;

        private static bool _isGameCaptureHooked = false;

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

                    if (formattedMessage.Contains("Starting capture"))
                        _isGameCaptureHooked = true;

                    if (formattedMessage.Contains("capture stopped"))
                        _isGameCaptureHooked = false;

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
                    Log.Error(e.StackTrace);
                }
            }), IntPtr.Zero);

            Log.Information("libobs version: " + obs_get_version_string());

            // Step 1: Call obs_startup() as per documentation
            if (!obs_startup("en-US", null, IntPtr.Zero))
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

            // Step 7: Log available encoders
            LogAvailableEncoders();

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

            return obs_reset_video(ref videoInfo) == 0; // Returns true if successful
        }

        public static bool StartRecording(string name = "Unknown", string exePath = "Unknown")
        {
            isInitializingRecording = true;
            bool isReplayBufferMode = Settings.Instance.RecordingMode == RecordingMode.Buffer;

            string fileName = Path.GetFileName(exePath);

            if ((isReplayBufferMode && bufferOutput != IntPtr.Zero) || (!isReplayBufferMode && output != IntPtr.Zero))
            {
                Log.Information($"{(isReplayBufferMode ? "Replay buffer" : "Recording")} is already in progress.");
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
            displaySource = obs_source_create("game_capture", "gameplay", videoSourceSettings, IntPtr.Zero);
            obs_data_release(videoSourceSettings);
            obs_set_output_source(0, displaySource);

            // Connect to 'hooked' and 'unhooked' signals for game capture
            IntPtr signalHandler = obs_source_get_signal_handler(displaySource);
            hookedCallback = new signal_callback_t(OnGameCaptureHooked);
            unhookedCallback = new signal_callback_t(OnGameCaptureUnhooked);
            signal_handler_connect(signalHandler, "hooked", hookedCallback, IntPtr.Zero);
            signal_handler_connect(signalHandler, "unhooked", unhookedCallback, IntPtr.Zero);

            bool hooked = WaitUntilGameCaptureHooks();

            if (!hooked)
            {
                Log.Error("Game Capture did not hook within 40 seconds.");
                DisposeSources();

                if (!Settings.Instance.EnableDisplayRecording)
                {
                    Log.Information("Display recording is disabled, stopping recording.");
                    StopRecording();
                    return false;
                }

                Log.Information("Using display capture instead");

                IntPtr displayCaptureSettings = obs_data_create();
                displaySource = obs_source_create("monitor_capture", "display", displayCaptureSettings, IntPtr.Zero);
                obs_data_release(displayCaptureSettings);
                obs_set_output_source(0, displaySource);
            }

            IntPtr videoEncoderSettings = obs_data_create();
            obs_data_set_string(videoEncoderSettings, "preset", "Quality");
            obs_data_set_string(videoEncoderSettings, "profile", "high");
            obs_data_set_bool(videoEncoderSettings, "use_bufsize", true);
            obs_data_set_string(videoEncoderSettings, "rate_control", Settings.Instance.RateControl);

            switch (Settings.Instance.RateControl)
            {
                case "CBR":
                    int videoBitrateKbps = Settings.Instance.Bitrate * 1000;
                    obs_data_set_int(videoEncoderSettings, "bitrate", (uint)videoBitrateKbps);
                    break;

                case "VBR":
                    videoBitrateKbps = Settings.Instance.Bitrate * 1000;
                    obs_data_set_int(videoEncoderSettings, "bitrate", (uint)videoBitrateKbps);
                    break;

                case "CRF":
                    obs_data_set_int(videoEncoderSettings, "crf", (uint)Settings.Instance.CrfValue);
                    break;

                case "CQP":
                    obs_data_set_int(videoEncoderSettings, "qp", (uint)Settings.Instance.CqLevel);
                    break;

                default:
                    throw new Exception("Unsupported Rate Control method.");
            }

            // Select the appropriate encoder based on settings and available hardware
            string encoderId = GetEncoderIdBasedOnSettings();
            videoEncoder = obs_video_encoder_create(encoderId, "Segra Recorder", videoEncoderSettings, IntPtr.Zero);
            obs_data_release(videoEncoderSettings);
            obs_encoder_set_video(videoEncoder, obs_get_video());

            if (Settings.Instance.InputDevices != null && Settings.Instance.InputDevices.Count > 0)
            {
                int audioSourceIndex = 1;

                foreach (var deviceSetting in Settings.Instance.InputDevices)
                {
                    if (!string.IsNullOrEmpty(deviceSetting.Id))
                    {
                        IntPtr micSettings = obs_data_create();
                        obs_data_set_string(micSettings, "device_id", deviceSetting.Id);

                        string sourceName = $"Microphone_{micSources.Count + 1}";
                        IntPtr micSource = obs_source_create("wasapi_input_capture", sourceName, micSettings, IntPtr.Zero);

                        obs_data_release(micSettings);

                        float volume = deviceSetting.Volume;
                        obs_source_set_volume(micSource, volume);

                        obs_set_output_source((uint)audioSourceIndex, micSource);
                        micSources.Add(micSource);

                        audioSourceIndex++;
                        Log.Information($"Added input device: {deviceSetting.Id} as {sourceName} with volume {volume}");
                    }
                }
            }

            if (!Settings.Instance.OnlyRecordGameAndDiscord && Settings.Instance.OutputDevices != null && Settings.Instance.OutputDevices.Count > 0)
            {
                int desktopSourceIndex = micSources.Count + 1;

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

            if (Settings.Instance.OnlyRecordGameAndDiscord)
            {
                IntPtr discordSettings = obs_data_create();
                obs_data_set_string(discordSettings, "window", "::Discord.exe");
                obs_data_set_int(discordSettings, "priority", 2); // match by exe
                //obs_data_set_bool(discordSettings, "capture_audio_only", true);
                IntPtr source = obs_source_create("wasapi_process_output_capture", "Discord Audio", discordSettings, IntPtr.Zero);
                obs_data_release(discordSettings);
                obs_set_output_source((uint)micSources.Count + 1, source);
                desktopSources.Add(source);

                IntPtr gameSettings = obs_data_create();
                string windowId = "::" + fileName;
                Log.Information($"Recording game: {fileName}");
                obs_data_set_string(gameSettings, "window", windowId);
                obs_data_set_int(gameSettings, "priority", 2); // match by exe
                IntPtr gameSource = obs_source_create("wasapi_process_output_capture", "Game Audio", gameSettings, IntPtr.Zero);
                obs_data_release(gameSettings);
                obs_set_output_source((uint)micSources.Count + 2, gameSource);
                desktopSources.Add(gameSource);
            }

            IntPtr audioEncoderSettings = obs_data_create();
            obs_data_set_int(audioEncoderSettings, "bitrate", 128);
            audioEncoder = obs_audio_encoder_create("ffmpeg_aac", "simple_aac_encoder", audioEncoderSettings, 0, IntPtr.Zero);
            obs_data_release(audioEncoderSettings);
            obs_encoder_set_audio(audioEncoder, obs_get_audio());

            // Determine content type and paths based on recording mode
            Content.ContentType contentType = Settings.Instance.RecordingMode == RecordingMode.Buffer
                ? Content.ContentType.Buffer
                : Content.ContentType.Session;

            string videoPath = Settings.Instance.ContentFolder + "/" + contentType.ToString().ToLower() + "s";
            if (!Directory.Exists(videoPath))
                Directory.CreateDirectory(videoPath);

            string videoOutputPath = $"{Settings.Instance.ContentFolder}/{contentType.ToString().ToLower()}s/{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";

            if (isReplayBufferMode)
            {
                // Set up replay buffer output
                IntPtr bufferOutputSettings = obs_data_create();
                obs_data_set_string(bufferOutputSettings, "directory", videoPath);
                obs_data_set_string(bufferOutputSettings, "format", "%CCYY-%MM-%DD_%hh-%mm-%ss");
                obs_data_set_string(bufferOutputSettings, "extension", "mp4");
                // Set replay buffer duration and max size from settings
                obs_data_set_int(bufferOutputSettings, "max_time_sec", (uint)Settings.Instance.ReplayBufferDuration);
                obs_data_set_int(bufferOutputSettings, "max_size_mb", (uint)Settings.Instance.ReplayBufferMaxSize);

                bufferOutput = obs_output_create("replay_buffer", "replay_buffer_output", bufferOutputSettings, IntPtr.Zero);
                obs_data_release(bufferOutputSettings);

                // Set encoders for replay buffer
                obs_output_set_video_encoder(bufferOutput, videoEncoder);
                obs_output_set_audio_encoder(bufferOutput, audioEncoder, 0);

                // Set up signal handlers for replay buffer
                IntPtr bufferOutputHandler = obs_output_get_signal_handler(bufferOutput);
                signal_handler_connect(bufferOutputHandler, "stop", outputStopCallback, IntPtr.Zero);
                signal_handler_connect(bufferOutputHandler, "saved", replaySavedCallback, IntPtr.Zero);
            }
            else
            {
                // Set up standard recording output
                IntPtr outputSettings = obs_data_create();
                obs_data_set_string(outputSettings, "path", videoOutputPath);
                obs_data_set_string(outputSettings, "format_name", "mp4");

                output = obs_output_create("ffmpeg_muxer", "simple_output", outputSettings, IntPtr.Zero);
                obs_data_release(outputSettings);

                // Set encoders for standard recording
                obs_output_set_video_encoder(output, videoEncoder);
                obs_output_set_audio_encoder(output, audioEncoder, 0);

                // Set up signal handler for standard recording
                signal_handler_connect(obs_output_get_signal_handler(output), "stop", outputStopCallback, IntPtr.Zero);
            }

            // Overwrite the file name with the hooked executable name if using game hook
            fileName = hookedExecutableFileName ?? fileName;
            
            _ = Task.Run(PlayStartSound);

            bool outputStarted;
            if (isReplayBufferMode)
            {
                // Start replay buffer
                outputStarted = obs_output_start(bufferOutput);
                if (!outputStarted)
                {
                    Log.Error($"Failed to start replay buffer: {obs_output_get_last_error(bufferOutput)}");
                    Settings.Instance.State.Recording = null;
                    MessageUtils.SendSettingsToFrontend();
                    return false;
                }

                Log.Information("Replay buffer started successfully");
            }
            else
            {
                // Start standard recording
                outputStarted = obs_output_start(output);
                if (!outputStarted)
                {
                    Log.Error($"Failed to start recording: {obs_output_get_last_error(output)}");
                    Settings.Instance.State.Recording = null;
                    return false;
                }
            }

            string? gameImage = GameIconUtils.ExtractIconAsBase64(exePath);

            Settings.Instance.State.Recording = new Recording()
            {
                StartTime = DateTime.Now,
                Game = name,
                FilePath = videoOutputPath,
                FileName = fileName,
                IsUsingGameHook = hooked,
                GameImage = gameImage
            };
            isInitializingRecording = false;
            MessageUtils.SendSettingsToFrontend();

            Log.Information("Recording started: " + videoOutputPath);
            if (!isReplayBufferMode)
            {
                GameIntegrationService.Start(name);
            }
            Task.Run(KeybindCaptureService.Start);
            return true;
        }

        public static void StopRecording()
        {
            bool isReplayBufferMode = Settings.Instance.RecordingMode == RecordingMode.Buffer;

            if (isReplayBufferMode && bufferOutput != IntPtr.Zero)
            {
                // Stop replay buffer
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

                GameIntegrationService.Shutdown();
                KeybindCaptureService.Stop();

                // Reload content list
                SettingsUtils.LoadContentFromFolderIntoState(false);
            }
            else if (!isReplayBufferMode && output != IntPtr.Zero)
            {
                // Stop standard recording
                if (Settings.Instance.State.Recording != null)
                    Settings.Instance.State.UpdateRecordingEndTime(DateTime.Now);

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

                GameIntegrationService.Shutdown();
                KeybindCaptureService.Stop();

                ContentUtils.CreateMetadataFile(Settings.Instance.State.Recording.FilePath, Content.ContentType.Session, Settings.Instance.State.Recording.Game, Settings.Instance.State.Recording.Bookmarks);
                ContentUtils.CreateThumbnail(Settings.Instance.State.Recording.FilePath, Content.ContentType.Session);

                if (Settings.Instance.State.Recording != null)
                {
                    Log.Information($"Recording details:");
                    Log.Information($"Start Time: {Settings.Instance.State.Recording.StartTime}");
                    Log.Information($"End Time: {Settings.Instance.State.Recording.EndTime}");
                    Log.Information($"Duration: {Settings.Instance.State.Recording.Duration}");
                    Log.Information($"File Path: {Settings.Instance.State.Recording.FilePath}");
                }

                SettingsUtils.LoadContentFromFolderIntoState(false);
            }
            else
            {
                DisposeOutput();
                DisposeSources();
                DisposeEncoders();
            }

            Task.Run(StorageUtils.EnsureStorageBelowLimit);

            // If the recording ends before it started, don't do anything
            if (Settings.Instance.State.Recording == null || Settings.Instance.State.Recording.FilePath == null)
            {
                return;
            }

            string fileName = Path.GetFileNameWithoutExtension(Settings.Instance.State.Recording.FilePath);

            Settings.Instance.State.Recording = null;
            hookedExecutableFileName = null;
            if (Settings.Instance.EnableAi && AuthService.IsAuthenticated() && Settings.Instance.AutoGenerateHighlights)
            {
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
                Log.Information($"Game hooked: Title='{Marshal.PtrToStringAnsi(title)}', Class='{Marshal.PtrToStringAnsi(windowClass)}', Executable='{Marshal.PtrToStringAnsi(executable)}'");

                // Overwrite the file name with the hooked one because sometimes the current tracked file name is the startup exe instead of the actual game 
                hookedExecutableFileName = Marshal.PtrToStringAnsi(executable);
                if (Settings.Instance.State.Recording != null && hookedExecutableFileName != null)
                {
                    Settings.Instance.State.Recording.FileName = hookedExecutableFileName;
                }

                if (Settings.Instance.OnlyRecordGameAndDiscord)
                {
                    IntPtr gameSource = IntPtr.Zero;
                    for (int i = 0; i < desktopSources.Count; i++)
                    {
                        if (obs_source_get_name(desktopSources[i]) == "Game Audio")
                        {
                            gameSource = desktopSources[i];
                            break;
                        }
                    }
                    
                    if (gameSource != IntPtr.Zero)
                    {
                        // Update the source to use the hooked executable
                        IntPtr gameSettings = obs_data_create();
                        string windowId = "::" + hookedExecutableFileName;
                        obs_data_set_string(gameSettings, "window", windowId);
                        obs_data_set_int(gameSettings, "priority", 2);
                        obs_source_update(gameSource, gameSettings);
                        obs_data_release(gameSettings);
                        Log.Information($"Updated Game Audio source to use hooked executable: {hookedExecutableFileName}");
                    }
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

        private static bool WaitUntilGameCaptureHooks(int timeoutMs = 80000)
        {
            int elapsed = 0;
            const int step = 100;

            while (!_isGameCaptureHooked)
            {
                Thread.Sleep(step);
                elapsed += step;
                if (elapsed >= timeoutMs)
                    return false;
            }

            return true;
        }

        public static void DisposeSources()
        {
            if (displaySource != IntPtr.Zero)
            {
                IntPtr signalHandler = obs_source_get_signal_handler(displaySource);

                if (hookedCallback != null)
                {
                    signal_handler_disconnect(signalHandler, "hooked", hookedCallback, IntPtr.Zero);
                    hookedCallback = null;
                }

                if (unhookedCallback != null)
                {
                    signal_handler_disconnect(signalHandler, "unhooked", unhookedCallback, IntPtr.Zero);
                    unhookedCallback = null;
                }

                obs_set_output_source(0, IntPtr.Zero);
                obs_source_release(displaySource);
                displaySource = IntPtr.Zero;
            }

            int micSourcesCount = micSources.Count;
            for (int i = 0; i < micSources.Count; i++)
            {
                if (micSources[i] != IntPtr.Zero)
                {
                    obs_set_output_source((uint)(i + 1), IntPtr.Zero);
                    obs_source_release(micSources[i]);
                    micSources[i] = IntPtr.Zero;
                }
            }
            micSources.Clear();

            for (int i = 0; i < desktopSources.Count; i++)
            {
                if (desktopSources[i] != IntPtr.Zero)
                {
                    int desktopIndex = i + micSourcesCount + 1;
                    obs_set_output_source((uint)desktopIndex, IntPtr.Zero);
                    obs_source_release(desktopSources[i]);
                    desktopSources[i] = IntPtr.Zero;
                }
            }
            desktopSources.Clear();
        }

        public static void DisposeEncoders()
        {
            if (videoEncoder != IntPtr.Zero)
            {
                obs_encoder_release(videoEncoder);
                videoEncoder = IntPtr.Zero;
            }

            if (audioEncoder != IntPtr.Zero)
            {
                obs_encoder_release(audioEncoder);
                audioEncoder = IntPtr.Zero;
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
            public string Sha { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("download_url")]
            public string DownloadUrl { get; set; }
        }

        private static string GetEncoderIdBasedOnSettings()
        {
            // Check if user wants CPU or GPU encoding
            if (Settings.Instance.Encoder.Equals("cpu", StringComparison.CurrentCultureIgnoreCase))
            {
                Log.Information("Using CPU encoder (x264)");
                return CPU_ENCODER;
            }

            // User wants GPU encoding, check which GPU vendor is available
            switch (DetectedGpuVendor)
            {
                case GpuVendor.Nvidia:
                    Log.Information("Using NVIDIA GPU encoder (NVENC)");
                    return NVIDIA_ENCODER;
                case GpuVendor.AMD:
                    Log.Information("Using AMD GPU encoder (AMF)");
                    return AMD_ENCODER;
                case GpuVendor.Intel:
                    Log.Information("Using Intel GPU encoder (QSV)");
                    return INTEL_ENCODER;
                default:
                    // Fall back to CPU encoding if no supported GPU is detected
                    Log.Warning("No supported GPU detected, falling back to CPU encoder (x264)");
                    return CPU_ENCODER;
            }
        }

        private static void DetectGpuVendor()
        {
            try
            {
                // Get GPU information using Windows Management Instrumentation (WMI)
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        string name = obj["Name"]?.ToString()?.ToLower() ?? string.Empty;

                        if (name.Contains("nvidia"))
                        {
                            DetectedGpuVendor = GpuVendor.Nvidia;
                            Log.Information($"Detected NVIDIA GPU: {obj["Name"]}");
                            return;
                        }
                        else if (name.Contains("amd") || name.Contains("radeon") || name.Contains("ati"))
                        {
                            DetectedGpuVendor = GpuVendor.AMD;
                            Log.Information($"Detected AMD GPU: {obj["Name"]}");
                            return;
                        }
                        else if (name.Contains("intel"))
                        {
                            DetectedGpuVendor = GpuVendor.Intel;
                            Log.Information($"Detected Intel GPU: {obj["Name"]}");
                            return;
                        }
                    }
                }

                Log.Warning("Could not identify GPU vendor, will default to CPU encoding if GPU encoding is selected");
            }
            catch (Exception ex)
            {
                Log.Error($"Error detecting GPU vendor: {ex.Message}");
                DetectedGpuVendor = GpuVendor.Unknown;
            }
        }

        private static void PlayStartSound()
        {
            using (var unmanagedStream = Properties.Resources.start)
            using (var memoryStream = new MemoryStream())
            {
                unmanagedStream.CopyTo(memoryStream);
                byte[] audioData = memoryStream.ToArray();

                using (var audioReader = new WaveFileReader(new MemoryStream(audioData)))
                using (var waveOut = new WaveOutEvent())
                {
                    var volumeStream = new VolumeWaveProvider16(audioReader)
                    {
                        Volume = 0.5f
                    };

                    waveOut.Init(volumeStream);
                    waveOut.Play();

                    while (waveOut.PlaybackState == PlaybackState.Playing)
                        Thread.Sleep(100);
                }
            }
        }
        private static void LogAvailableEncoders()
        {
            Log.Information("Available encoders:");

            // Enumerate all encoder types
            string encoderId = string.Empty;
            size_t idx = 0;

            while (obs_enum_encoder_types(idx, ref encoderId))
            {
                Log.Information($"  - Encoder: {encoderId}");
                idx++;
            }

            Log.Information($"Total encoders found: {idx}");
        }
    }
}