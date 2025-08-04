using LibObs;
using NAudio.Wave;
using Segra.Backend.Models;
using Segra.Backend.Services;
using Serilog;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using static LibObs.Obs;
using static Segra.Backend.Utils.GeneralUtils;
using size_t = System.UIntPtr;

namespace Segra.Backend.Utils
{
    public static class OBSUtils
    {
        public static bool IsInitialized { get; private set; }
        public static GpuVendor DetectedGpuVendor { get; private set; } = DetectGpuVendor();
        static bool signalOutputStop = false;
        static IntPtr output = IntPtr.Zero;
        static IntPtr bufferOutput = IntPtr.Zero;
        static bool replaySaved = false;
        static IntPtr gameCaptureSource = IntPtr.Zero;
        public static IntPtr MonitorCaptureSource { get; private set; } = IntPtr.Zero;
        static IntPtr windowCaptureSource = IntPtr.Zero;
        static List<IntPtr> micAudioSources = new List<IntPtr>();
        static List<IntPtr> desktopAudioSources = new List<IntPtr>();
        static IntPtr videoEncoder = IntPtr.Zero;
        static IntPtr audioEncoder = IntPtr.Zero;
        public static Process? ProcessToRecord { get; set; }
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

        // Variable to store the replay buffer path extracted from logs
        private static string? _lastReplayBufferPath;
        private static signal_callback_t? hookedCallback;
        private static signal_callback_t? unhookedCallback;

        private static bool _isGameCaptureHooked = false;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool QueryFullProcessImageName(
            IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        public static (int width, int height)? ClientSize(Process? p)
        {
            if(p == null)
                return null;

            p.Refresh();
            IntPtr hwnd = p.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                return null;

            RECT r;
            return GetClientRect(hwnd, out r)
                ? ((r.Right - r.Left), (r.Bottom - r.Top))
                : null;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

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
            Task.Run(() => ContentUtils.CreateAudioFile(savedPath, Content.ContentType.Buffer));

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

                    if (formattedMessage.Contains("attempting to hook"))
                    {
                        if (Settings.Instance.State.PreRecording != null)
                        {
                            Settings.Instance.State.PreRecording.Status = "Waiting for game hook";
                            _ = MessageUtils.SendSettingsToFrontend("Waiting for game hook");
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

        private static bool ResetVideoSettings(uint? gameWidth = null, uint? gameHeight = null)
        {
            SettingsUtils.GetPrimaryMonitorResolution(out uint monitorWidth, out uint monitorHeight);

            obs_video_info videoInfo = new()
            {
                adapter = 0,
                graphics_module = "libobs-d3d11",
                fps_num = (uint)Settings.Instance.FrameRate,
                fps_den = 1,
                base_width = gameWidth ?? monitorWidth,
                base_height = gameHeight ?? monitorHeight,
                output_width = gameWidth ?? monitorWidth,
                output_height = gameHeight ?? monitorHeight,
                output_format = video_format.VIDEO_FORMAT_NV12,
                gpu_conversion = true,
                colorspace = video_colorspace.VIDEO_CS_DEFAULT,
                range = video_range_type.VIDEO_RANGE_DEFAULT,
                scale_type = obs_scale_type.OBS_SCALE_BILINEAR
            };

            return obs_reset_video(ref videoInfo) == 0; // Returns true if successful
        }

        public static bool StartRecording(string name = "Manual Recording", string? exePath = null, bool startManually = false)
        {
            signalOutputStop = false;
            _isGameCaptureHooked = false;

            // Set pre-recording state
            Settings.Instance.State.PreRecording = new PreRecording { Game = name, Status = "Waiting to start" };

            // Get file name if not manually started
            string? fileName = null;
            if (!startManually && exePath != null)
            {
                fileName = Path.GetFileName(exePath);
            }

            // Check if recording is already in progress
            bool isReplayBufferMode = Settings.Instance.RecordingMode == RecordingMode.Buffer;
            if ((isReplayBufferMode && bufferOutput != IntPtr.Zero) || (!isReplayBufferMode && output != IntPtr.Zero))
            {
                Log.Information($"{(isReplayBufferMode ? "Replay buffer" : "Recording")} is already in progress.");
                Settings.Instance.State.PreRecording = null;
                return false;
            }

            string? windowString = null;
            bool isFullScreenGame = true;
            if (!startManually)
            {
                // When the recording is started automatically fileName should not be null
                if (fileName == null)
                {
                    Settings.Instance.State.Recording = null;
                    Settings.Instance.State.PreRecording = null;
                    _ = MessageUtils.SendSettingsToFrontend("File name is null even though it should not be");
                    StopRecording();
                    return false;
                }
                
                // Get process from exe name, this is used in ClientSize to get the game window size
                ProcessToRecord = GetProcessFromExe(fileName);
                (int width, int height)? initialClientSize = ClientSize(ProcessToRecord);
                
                // Wait for the game to start before starting the recording
                bool success = WaitForGameToStart();
                if (!success)
                {
                    Settings.Instance.State.Recording = null;
                    Settings.Instance.State.PreRecording = null;
                    _ = MessageUtils.SendSettingsToFrontend("Game did not start within the timeout period");
                    StopRecording();
                    return false;
                }

                // The initial client size is the size of the game window the first time it detects the game, if it is invalid or zero it means the game has probably just started and needs some time to be fully shown
                if (initialClientSize == null || initialClientSize?.width == 0 || initialClientSize?.height == 0)
                {
                    Log.Warning("Initial client size is invalid or zero. waiting 3 seconds to make sure the window size has fully been shown.");
                    Task.Delay(3000).Wait();
                }

                // Get the game window client size and reset video settings to set correct output width for games with custom resolution
                (int width, int height)? gameWindowClientSize = ClientSize(ProcessToRecord);
                if (gameWindowClientSize is (int width, int height))
                {
                    _ = ResetVideoSettings(
                        gameWidth: (uint)width,
                        gameHeight: (uint)height);
                }
                else
                {
                    Settings.Instance.State.Recording = null;
                    Settings.Instance.State.PreRecording = null;
                    _ = MessageUtils.SendSettingsToFrontend("Failed to get game window client size after game started");
                    StopRecording();
                    return false;
                }
                
                // Better safe than sorry, wait a bit to make sure the video reset has finished. This might not be needed.
                Task.Delay(500).Wait();

                // Check if the game is fullscreen
                SettingsUtils.GetPrimaryMonitorResolution(out uint primaryMonitorWidth, out uint primaryMonitorHeight);
                isFullScreenGame = primaryMonitorWidth == gameWindowClientSize?.width && primaryMonitorHeight == gameWindowClientSize?.height;

                // If the client size matches the primary monitor size or we can't get the hooked process, use monitor capture
                // This is due to fullscreen games not having a DWM window (and therefore not being able to be captured using window capture)
                if (isFullScreenGame || ProcessToRecord == null)
                {
                    if (ProcessToRecord == null) {
                        Log.Warning("Hooked process is null, using monitor capture");
                    }
                    else {
                        Log.Information("Game is fullscreen, using monitor capture");
                    }
                    AddMonitorCapture();
                }
                else
                {
                    Log.Information("Game is not fullscreen, using window capture");
                    windowString = WindowStringFromProcess(ProcessToRecord);

                    // If we can't get the window string, use monitor capture
                    if (windowString != null)
                    {
                        AddWindowCaptureIfNotHooked(windowString);
                    }
                    else {
                        Log.Warning("Failed to get window string from process, using monitor capture");
                        AddMonitorCapture();
                    }
                }
            }

            // If we start manually, we use monitor capture as we don't have a process to record
            if (startManually) {
                _ = ResetVideoSettings();
                Task.Delay(500).Wait();
                AddMonitorCapture();
            }

            // Setup game capture source
            IntPtr gameCaptureSourceSettings = obs_data_create();
            if(isFullScreenGame || startManually || windowString == null)
            {
                // If the game is fullscreen or we start manually or we can't get the window string
                if (startManually)
                {
                    Log.Information("Manually recording, using any_fullscreen for game_capture source");
                }
                else if (windowString == null)
                {
                    Log.Warning("Window string is null, using any_fullscreen for game_capture source");
                }
                else
                {
                    Log.Information("Game is fullscreen, using any_fullscreen for game_capture source");
                }
                obs_data_set_string(gameCaptureSourceSettings, "capture_mode", "any_fullscreen");
            }
            else
            {
                // If the game is not fullscreen or we can't get the window string or we start manually, we use window for the game capture source
                Log.Information("Game is not fullscreen, using window for game_capture source");
                obs_data_set_string(gameCaptureSourceSettings, "capture_mode", "window");
                obs_data_set_string(gameCaptureSourceSettings, "window", windowString);
                obs_data_set_string(gameCaptureSourceSettings, "capture_window", windowString);
            }

            gameCaptureSource = obs_source_create("game_capture", "gameplay", gameCaptureSourceSettings, IntPtr.Zero);
            obs_data_release(gameCaptureSourceSettings);
            obs_set_output_source(0, gameCaptureSource);

            // Connect to 'hooked' and 'unhooked' signals for game capture
            // These are used to detect when the game capture source hooks and unhooks
            IntPtr signalHandler = obs_source_get_signal_handler(gameCaptureSource);
            hookedCallback = new signal_callback_t(OnGameCaptureHooked);
            unhookedCallback = new signal_callback_t(OnGameCaptureUnhooked);
            signal_handler_connect(signalHandler, "hooked", hookedCallback, IntPtr.Zero);
            signal_handler_connect(signalHandler, "unhooked", unhookedCallback, IntPtr.Zero);

            // If display recording is disabled, wait for game capture to hook before starting recording
            if (!Settings.Instance.EnableDisplayRecording)
            {
                // Set timeout duration:
                // - 90 seconds if started manually (this timeout starts BEFORE the game launches, therefore it may need more time)
                // - 20 seconds if NOT started manually (this timeout starts AFTER the game has fully launched)
                int timeoutMs = startManually ? 90_000 : 20_000;

                // Wait for game capture to hook
                bool hooked = WaitUntilGameCaptureHooks(timeoutMs);
                if (!hooked)
                {
                    // Prevent retry recording to prevent infinite loop, this flag resets when the user switches foreground window
                    GameDetectionService.PreventRetryRecording = true;

                    // Reset recording state
                    Settings.Instance.State.Recording = null;
                    Settings.Instance.State.PreRecording = null;

                    // Send settings to frontend
                    _ = MessageUtils.SendSettingsToFrontend("Game did not hook within the timeout period");
                    StopRecording();
                    return false;
                }
            }

            // If display capture is enabled, start a timer to check if game capture hooks within 90 seconds
            // This is used to remove the game capture source if it doesn't hook within the timeout period
            if (Settings.Instance.EnableDisplayRecording)
            {
                StartGameCaptureHookTimeoutTimer();
            }

            // Create video encoder settings
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
                    Settings.Instance.State.PreRecording = null;
                    throw new Exception("Unsupported Rate Control method.");
            }

            // Select the appropriate encoder based on settings and available hardware
            Log.Information($"Using encoder: {Settings.Instance.Codec!.FriendlyName} ({Settings.Instance.Codec.InternalEncoderId})");
            string encoderId = Settings.Instance.Codec!.InternalEncoderId;
            videoEncoder = obs_video_encoder_create(encoderId, "Segra Recorder", videoEncoderSettings, IntPtr.Zero);
            obs_data_release(videoEncoderSettings);
            obs_encoder_set_video(videoEncoder, obs_get_video());

            // Setup audio sources
            if (Settings.Instance.InputDevices != null && Settings.Instance.InputDevices.Count > 0)
            {
                int audioSourceIndex = 2;

                foreach (var deviceSetting in Settings.Instance.InputDevices)
                {
                    if (!string.IsNullOrEmpty(deviceSetting.Id))
                    {
                        IntPtr micSettings = obs_data_create();
                        obs_data_set_string(micSettings, "device_id", deviceSetting.Id);

                        string sourceName = $"Microphone_{micAudioSources.Count + 1}";
                        IntPtr micSource = obs_source_create("wasapi_input_capture", sourceName, micSettings, IntPtr.Zero);

                        obs_data_release(micSettings);

                        float volume = deviceSetting.Volume;
                        obs_source_set_volume(micSource, volume);

                        obs_set_output_source((uint)audioSourceIndex, micSource);
                        micAudioSources.Add(micSource);

                        audioSourceIndex++;
                        Log.Information($"Added input device: {deviceSetting.Id} as {sourceName} with volume {volume}");
                    }
                }
            }

            // Setup output devices
            if (Settings.Instance.OutputDevices != null && Settings.Instance.OutputDevices.Count > 0)
            {
                int desktopSourceIndex = micAudioSources.Count + 2;

                foreach (var deviceSetting in Settings.Instance.OutputDevices)
                {
                    if (!string.IsNullOrEmpty(deviceSetting.Id))
                    {
                        IntPtr desktopSettings = obs_data_create();
                        obs_data_set_string(desktopSettings, "device_id", deviceSetting.Id);

                        string sourceName = $"DesktopAudio_{desktopAudioSources.Count + 1}";
                        IntPtr desktopSource = obs_source_create("wasapi_output_capture", sourceName, desktopSettings, IntPtr.Zero);

                        obs_data_release(desktopSettings);

                        float desktopVolume = 1.0f; // Use fixed volume (100%)
                        obs_source_set_volume(desktopSource, desktopVolume);

                        obs_set_output_source((uint)desktopSourceIndex, desktopSource);
                        desktopAudioSources.Add(desktopSource);

                        desktopSourceIndex++;
                        Log.Information($"Added output device: {deviceSetting.Name} ({deviceSetting.Id}) as {sourceName} with fixed volume {desktopVolume}");
                    }
                }
            }

            // Setup audio encoder
            IntPtr audioEncoderSettings = obs_data_create();
            obs_data_set_int(audioEncoderSettings, "bitrate", 128);
            audioEncoder = obs_audio_encoder_create("ffmpeg_aac", "simple_aac_encoder", audioEncoderSettings, 0, IntPtr.Zero);
            obs_data_release(audioEncoderSettings);
            obs_encoder_set_audio(audioEncoder, obs_get_audio());

            // Determine content type and paths based on recording mode
            Content.ContentType contentType = Settings.Instance.RecordingMode == RecordingMode.Buffer
                ? Content.ContentType.Buffer
                : Content.ContentType.Session;

            // Create content folder if it doesn't exist
            string videoPath = Settings.Instance.ContentFolder + "/" + contentType.ToString().ToLower() + "s";
            if (!Directory.Exists(videoPath))
                Directory.CreateDirectory(videoPath);

            // Null if using replay buffer mode
            string? videoOutputPath = null;
            
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
                // Create video output path
                videoOutputPath = $"{Settings.Instance.ContentFolder}/{contentType.ToString().ToLower()}s/{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
                Log.Information("Recording path: " + videoOutputPath);

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
            // fileName = hookedExecutableFileName ?? fileName;

            // Play start sound
            _ = Task.Run(PlayStartSound);

            if (isReplayBufferMode)
            {
                // Start replay buffer
                bool outputStarted = obs_output_start(bufferOutput);
                if (!outputStarted)
                {
                    Log.Error($"Failed to start replay buffer: {obs_output_get_last_error(bufferOutput)}");
                    Settings.Instance.State.Recording = null;
                    Settings.Instance.State.PreRecording = null;
                    _ = MessageUtils.SendSettingsToFrontend("Failed to start replay buffer");
                    StopRecording();
                    return false;
                }

                Log.Information("Replay buffer started successfully");
            }
            else
            {
                // Start standard recording
                bool outputStarted = obs_output_start(output);
                if (!outputStarted)
                {
                    Log.Error($"Failed to start recording: {obs_output_get_last_error(output)}");
                    Settings.Instance.State.Recording = null;
                    Settings.Instance.State.PreRecording = null;
                    StopRecording();
                    return false;
                }

                Log.Information("Recording started successfully");
            }

            // Get game image if we know the game exe path
            string? gameImage = null;
            if (exePath != null)
            {
                gameImage = GameIconUtils.ExtractIconAsBase64(exePath);
            }

            // Set recording state and remove pre recording state and send to frontend
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

            // Start game integration and keybind capture
            if (!isReplayBufferMode)
            {
                _ = GameIntegrationService.Start(name);
            }
            Task.Run(KeybindCaptureService.Start);
            return true;
        }

        // Adds window capture with the given window string
        public static void AddWindowCaptureIfNotHooked(string windowString)
        {
            if (Settings.Instance.EnableDisplayRecording && !_isGameCaptureHooked)
            {
                IntPtr windowCaptureSettings = obs_data_create();
                obs_data_set_string(windowCaptureSettings, "window", windowString);
                windowCaptureSource = obs_source_create("window_capture", "window", windowCaptureSettings, IntPtr.Zero);
                obs_data_release(windowCaptureSettings);
                obs_set_output_source(1, windowCaptureSource);
            }
        }

        // Adds monitor capture with the selected display
        public static void AddMonitorCapture()
        {
            if (Settings.Instance.EnableDisplayRecording && !_isGameCaptureHooked)
            {
                IntPtr displayCaptureSettings = obs_data_create();
                
                if(Settings.Instance.SelectedDisplay != null)
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
                MonitorCaptureSource = obs_source_create("monitor_capture", "display", displayCaptureSettings, IntPtr.Zero);
                obs_data_release(displayCaptureSettings);
                obs_set_output_source(1, MonitorCaptureSource);
            }
        }

        // Stops the recording
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

                Log.Information("Recording stopped and disposed.");

                // Stop game integration and keybind capture
                _ = GameIntegrationService.Shutdown();
                KeybindCaptureService.Stop();

                // Create metadata, thumbnail and audio file
                ContentUtils.CreateMetadataFile(Settings.Instance.State.Recording!.FilePath!, Content.ContentType.Session, Settings.Instance.State.Recording.Game, Settings.Instance.State.Recording.Bookmarks);
                ContentUtils.CreateThumbnail(Settings.Instance.State.Recording!.FilePath!, Content.ContentType.Session);
                Task.Run(() => ContentUtils.CreateAudioFile(Settings.Instance.State.Recording!.FilePath!, Content.ContentType.Session));

                // Log recording details
                Log.Information($"Recording details:");
                Log.Information($"Start Time: {Settings.Instance.State.Recording!.StartTime}");
                Log.Information($"End Time: {Settings.Instance.State.Recording!.EndTime}");
                Log.Information($"Duration: {Settings.Instance.State.Recording!.Duration}");
                Log.Information($"File Path: {Settings.Instance.State.Recording!.FilePath}");

                // Reload content list
                SettingsUtils.LoadContentFromFolderIntoState(false);
            }
            else
            {
                // Recording was stopped before it started, dispose all resources and reset state
                DisposeOutput();
                DisposeSources();
                DisposeEncoders();
                Settings.Instance.State.Recording = null;
                Settings.Instance.State.PreRecording = null;
            }

            Task.Run(StorageUtils.EnsureStorageBelowLimit);

            // If the recording ends before it started, don't do anything
            if (Settings.Instance.State.Recording == null || Settings.Instance.State.Recording.FilePath == null)
            {
                return;
            }

            string fileName = Path.GetFileNameWithoutExtension(Settings.Instance.State.Recording.FilePath);
            
            // Reset state
            Settings.Instance.State.Recording = null;
            hookedExecutableFileName = null;

            // Analyze video if AI is enabled and authenticated and not in replay buffer mode
            if (Settings.Instance.EnableAi && AuthService.IsAuthenticated() && Settings.Instance.AutoGenerateHighlights && !isReplayBufferMode)
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
                StopGameCaptureHookTimeoutTimerIfExists();
                DisposeMonitorCaptureSourceIfExists();
                DisposeWindowCaptureSourceIfExists();
                Log.Information($"Game hooked: Title='{Marshal.PtrToStringAnsi(title)}', Class='{Marshal.PtrToStringAnsi(windowClass)}', Executable='{Marshal.PtrToStringAnsi(executable)}'");
                
                // Overwrite the file name with the hooked one because sometimes the current tracked file name is the startup exe instead of the actual game 
                hookedExecutableFileName = Marshal.PtrToStringAnsi(executable);
                if (Settings.Instance.State.Recording != null)
                {
                    if(hookedExecutableFileName != null)
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

        private static bool WaitForGameToStart(int timeoutMs = 80000)
        {
            int elapsed = 0;
            const int step = 100;
            (int width, int height)? clientSize = null;

            while (ProcessToRecord?.MainWindowHandle == IntPtr.Zero ||
                   clientSize == null ||
                   clientSize.Value.width == 0 ||
                   clientSize.Value.height == 0)
            {
                ProcessToRecord?.Refresh();

                if (ProcessToRecord?.HasExited == true)
                {
                    Log.Warning("Process has exited before game started.");
                    return false;
                }

                clientSize = ClientSize(ProcessToRecord);

                Thread.Sleep(step);
                elapsed += step;
                Log.Information(
                    "Waiting for game to start - PreRecording Status: {PreRecordingStatus}, " +
                    "Executable Path: {ExecutablePath}, MainWindowHandle: {MainWindowHandle}, ClientSize: {ClientSize}",
                    Settings.Instance.State.PreRecording?.Status,
                    ProcessToRecord?.MainModule?.FileName,
                    ProcessToRecord?.MainWindowHandle,
                    clientSize.HasValue ? $"{clientSize.Value.width}x{clientSize.Value.height}" : "null");
                if (elapsed >= timeoutMs)
                {
                    Log.Warning(
                        "Timed out waiting for pre-recording status to be 'Waiting for game hook', " +
                        "main window handle to be available, and client size to be non-zero within {Seconds} seconds.",
                        timeoutMs / 1000);
                    return false;
                }
            }

            return true;
        }

        private static bool WaitUntilGameCaptureHooks(int timeoutMs)
        {
            int elapsed = 0;
            const int step = 100;

            while (!_isGameCaptureHooked)
            {
                Thread.Sleep(step);
                elapsed += step;
                if (elapsed >= timeoutMs)
                {
                    Log.Warning("Game Capture did not hook within {Seconds} seconds.", timeoutMs / 1000);
                    return false;
                }
            }

            return true;
        }

        public static void DisposeSources()
        {
            DisposeMonitorCaptureSourceIfExists();
            DisposeWindowCaptureSourceIfExists();
            DisposeGameCaptureSourceIfExists();

            int micSourcesCount = micAudioSources.Count;
            DisposeMicAudioSources();
            DisposeDesktopAudioSources(micSourcesCount);
        }

        public static void DisposeDesktopAudioSources(int micSourcesCount)
        {
            for (int i = 0; i < desktopAudioSources.Count; i++)
            {
                if (desktopAudioSources[i] != IntPtr.Zero)
                {
                    int desktopIndex = i + micSourcesCount + 2;
                    obs_set_output_source((uint)desktopIndex, IntPtr.Zero);
                    obs_source_release(desktopAudioSources[i]);
                    desktopAudioSources[i] = IntPtr.Zero;
                }
            }
            desktopAudioSources.Clear();
        }

        public static void DisposeMicAudioSources()
        {
            for (int i = 0; i < micAudioSources.Count; i++)
            {
                if (micAudioSources[i] != IntPtr.Zero)
                {
                    obs_set_output_source((uint)(i + 2), IntPtr.Zero);
                    obs_source_release(micAudioSources[i]);
                    micAudioSources[i] = IntPtr.Zero;
                }
            }
            micAudioSources.Clear();
        }

        public static void DisposeGameCaptureSourceIfExists()
        {
            if (gameCaptureSource != IntPtr.Zero)
            {
                obs_set_output_source(0, IntPtr.Zero);
                obs_source_release(gameCaptureSource);
                gameCaptureSource = IntPtr.Zero;
            }
            // Dispose the timer if it exists
            StopGameCaptureHookTimeoutTimerIfExists();
        }

        private static void StartGameCaptureHookTimeoutTimer()
        {
            // Dispose any existing timer first
            StopGameCaptureHookTimeoutTimerIfExists();
            
            // Create a new timer that checks after 90 seconds
            gameCaptureHookTimeoutTimer = new System.Threading.Timer(
                CheckGameCaptureHookStatus,
                null,
                90000, // 90 seconds delay
                Timeout.Infinite // Don't repeat
            );
            
            Log.Information("Started game capture hook timer (90 seconds)");
        }

        private static void StopGameCaptureHookTimeoutTimerIfExists()
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
                DisposeGameCaptureSourceIfExists();
            }
            else
            {
                Log.Information("Game capture hook check completed. Hook status: {0}", _isGameCaptureHooked ? "Hooked" : "Not hooked");
                // Just stop the timer without disposing the game capture source if it's hooked
                StopGameCaptureHookTimeoutTimerIfExists();
            }
        }

        public static void DisposeMonitorCaptureSourceIfExists()
        {
            if (MonitorCaptureSource != IntPtr.Zero)
            {
                obs_set_output_source(1, IntPtr.Zero);
                obs_source_release(MonitorCaptureSource);
                MonitorCaptureSource = IntPtr.Zero;
            }
        }

        public static void DisposeWindowCaptureSourceIfExists()
        {
            if (windowCaptureSource != IntPtr.Zero)
            {
                obs_set_output_source(1, IntPtr.Zero);
                obs_source_release(windowCaptureSource);
                windowCaptureSource = IntPtr.Zero;
            }
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
            public required string Sha { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("download_url")]
            public required string DownloadUrl { get; set; }
        }

        public static Process? GetProcessFromExe(string exeFileName)
        {
            string exeWanted = Path.GetFileName(exeFileName);

            foreach (Process p in Process.GetProcesses())
            {
                string exeActual = SafeExeName(p);
                if (exeActual == null ||
                    !exeWanted.Equals(exeActual, StringComparison.OrdinalIgnoreCase))
                    continue;

                return p;
            }

            return null;
        }

        public static string? WindowStringFromProcess(Process process)
        {
            if (process == null)
                return null;

            // A process can exist before its main window appears
            const int MaxWaitSeconds = 90;
            const int PollIntervalMs = 500; // Check every 500ms
            int elapsedMs = 0;

            // Non-blocking loop to wait for the main window to appear
            while (process.MainWindowHandle == IntPtr.Zero)
            {
                if (process.HasExited)
                {
                    Log.Warning($"Process '{process.ProcessName}' exited before main window appeared.");
                    return null;
                }

                // Check for timeout
                if (elapsedMs >= MaxWaitSeconds * 1000)
                {
                    Log.Warning($"Timeout waiting for main window of process '{process.ProcessName}' after {MaxWaitSeconds} seconds.");
                    return null;
                }

                Thread.Sleep(PollIntervalMs);
                elapsedMs += PollIntervalMs;

                // Refresh again after delay to check for updates
                process.Refresh();
            }

            IntPtr hwnd = process.MainWindowHandle;

            const int CAP = 256;
            var sb = new StringBuilder(CAP);

            if (GetWindowTextW(hwnd, sb, CAP) == 0)  // title
                return null;
            string title = sb.ToString();

            sb.Clear();
            if (GetClassNameW(hwnd, sb, CAP) == 0)   // class
                return null;
            string cls = sb.ToString();

            // Replace any ':' so we don’t break OBS’s delimiter
            return $"{title.Replace(':', '⸗')}:{cls.Replace(':', '⸗')}:{process.ProcessName}.exe";
        }

        public static string SafeExeName(Process p)
        {
           IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
           if (h == IntPtr.Zero) return null;

           var sb = new StringBuilder(260);
           int len = sb.Capacity;
           return QueryFullProcessImageName(h, 0, sb, ref len)
                  ? Path.GetFileName(sb.ToString())
                  : null;
        }

        public static string SafeFullPath(Process p)
        {
           IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
           if (h == IntPtr.Zero) return null;

           var sb = new StringBuilder(260);
           int len = sb.Capacity;
           return QueryFullProcessImageName(h, 0, sb, ref len)
                  ? sb.ToString()
                  : null;
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

        private static readonly Dictionary<string, string> EncoderFriendlyNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // ── NVIDIA NVENC ────────────────────────────────────
                ["jim_nvenc"]          = "NVIDIA NVENC H.264",
                ["jim_hevc_nvenc"]     = "NVIDIA NVENC H.265",
                ["jim_av1_nvenc"]      = "NVIDIA NVENC AV1",

                // ── AMD AMF ────────────────────────────────────────
                ["h264_texture_amf"]   = "AMD AMF H.264",
                ["h265_texture_amf"]   = "AMD AMF H.265",
                ["av1_texture_amf"]    = "AMD AMF AV1",

                // ── Intel Quick Sync ───────────────────────────────
                ["obs_qsv11_v2"]       = "Intel QSV H.264",
                ["obs_qsv11_hevc"]     = "Intel QSV H.265",
                ["obs_qsv11_av1"]      = "Intel QSV AV1",

                // ── CPU / software paths ───────────────────────────
                ["obs_x264"]           = "Software x264",
                ["ffmpeg_svt_av1"]     = "Software SVT-AV1",
                ["ffmpeg_aom_av1"]     = "Software AOM AV1",
                ["ffmpeg_openh264"]    = "Software OpenH264",
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
                if(name != null)
                {
                    Settings.Instance.State.Codecs.Add(new Codec { InternalEncoderId = encoderId, FriendlyName = friendlyName, IsHardwareEncoder = isHardware });
                }
                idx++;
            }

            Log.Information($"Total encoders found: {idx}");

            if(Settings.Instance.Codec == null)
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