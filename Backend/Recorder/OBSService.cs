using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ObsKit.NET;
using ObsKit.NET.Encoders;
using ObsKit.NET.Native.Types;
using ObsKit.NET.Outputs;
using ObsKit.NET.Scenes;
using ObsKit.NET.Signals;
using ObsKit.NET.Sources;
using Segra.Backend.Core.Models;
using Segra.Backend.Services;
using Segra.Backend.Shared;
using Serilog;
using System.IO.Compression;
using System.Text.RegularExpressions;
using static Segra.Backend.Utils.GeneralUtils;
using static Segra.Backend.App.MessageService;
using System.Net.Http.Json;
using Segra.Backend.Media;
using Segra.Backend.App;
using Segra.Backend.Windows.Display;
using Segra.Backend.Games;
using Segra.Backend.Windows.Input;
using Segra.Backend.Windows.Storage;
using System.Threading.Channels;

namespace Segra.Backend.Recorder
{
    public static partial class OBSService
    {
        // Constants
        private const uint OBS_SOURCE_FLAG_FORCE_MONO = 1u << 1; // from obs.h

        // Executables that OBS internally blacklists from game capture (cannot be hooked)
        // https://github.com/obsproject/obs-studio/blob/e448c0a963eda45f48515b2cb9a631daced9d503/plugins/win-capture/game-capture.c#L956
        private static readonly string[] ObsInternalBlacklist =
        [
            "explorer.exe",
            "steam.exe",
            "battle.net.exe",
            "galaxyclient.exe",
            "skype.exe",
            "uplay.exe",
            "origin.exe",
            "devenv.exe",
            "taskmgr.exe",
            "chrome.exe",
            "discord.exe",
            "firefox.exe",
            "systemsettings.exe",
            "applicationframehost.exe",
            "cmd.exe",
            "shellexperiencehost.exe",
            "winstore.app.exe",
            "searchui.exe",
            "lockapp.exe",
            "windowsinternal.composableshell.experiences.textinput.inputapp.exe"
        ];

        // Regex patterns for buffer parsing
        [GeneratedRegex(@"BufferDesc\.Width:\s*(\d+)")]
        private static partial Regex BufferDescWidthRegex();

        [GeneratedRegex(@"BufferDesc\.Height:\s*(\d+)")]
        private static partial Regex BufferDescHeightRegex();

        // Public properties
        public static bool IsInitialized { get; private set; }
        public static GpuVendor DetectedGpuVendor { get; private set; } = DetectGpuVendor();
        public static uint? CapturedWindowWidth { get; private set; } = null;
        public static uint? CapturedWindowHeight { get; private set; } = null;
        public static string? InstalledOBSVersion { get; private set; } = null;

        // OBS context
        private static ObsContext? _obsContext;

        // OBS scene
        private static Scene? _mainScene;
        private static SceneItem? _gameCaptureItem;
        private static SceneItem? _displayItem;

        // OBS output resources
        private static RecordingOutput? _output;
        private static ReplayBuffer? _bufferOutput;

        // OBS source resources
        public static GameCapture? GameCaptureSource { get; set; }
        private static MonitorCapture? _displaySource;
        private static readonly List<AudioInputCapture> _micSources = [];
        private static readonly List<AudioOutputCapture> _desktopSources = [];

        // OBS encoder resources
        private static VideoEncoder? _videoEncoder;
        private static readonly List<AudioEncoder> _audioEncoders = [];

        // Game capture state
        private static string? _hookedExecutableFileName;
        private static System.Threading.Timer? _gameCaptureHookTimeoutTimer = null;
        private static bool _isStillHookedAfterUnhook = false;

        // Recording/output state
        private static bool _isStoppingOrStopped = false;
        private static uint _currentBaseWidth;
        private static uint _currentBaseHeight;

        // Replay buffer state
        private static bool _replaySaved = false;

        // Signal connection for replay buffer saved event
        private static SignalConnection? _replaySavedConnection;

        /// <summary>
        /// Gets whether the game capture is currently hooked.
        /// Uses the built-in IsHooked property from OBSKit.NET.
        /// </summary>
        private static bool IsGameCaptureHooked => GameCaptureSource?.IsHooked ?? false;

        // Threading primitives
        private static readonly SemaphoreSlim _stopRecordingSemaphore = new SemaphoreSlim(1, 1);

        // Log processing queue - prevents OBS thread from blocking on log operations
        private static readonly Channel<(int level, string message)> _logChannel =
            Channel.CreateUnbounded<(int, string)>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        public static async Task<bool> SaveReplayBuffer()
        {
            // Check if replay buffer is active before trying to save
            if (_bufferOutput == null || !_bufferOutput.IsActive)
            {
                Log.Warning("Cannot save replay buffer: buffer is not active");
                return false;
            }

            Log.Information("Attempting to save replay buffer...");
            _replaySaved = false;

            try
            {
                _bufferOutput.Save();
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to save replay buffer: {ex.Message}");
                return false;
            }

            // Wait for the save callback to complete (up to 5 seconds)
            Log.Information("Waiting for replay buffer saved callback...");
            int attempts = 0;
            while (!_replaySaved && attempts < 50)
            {
                await Task.Delay(100);
                attempts++;
            }

            if (!_replaySaved)
            {
                Log.Warning("Replay buffer may not have saved correctly");
                return false;
            }

            string? savedPath = _bufferOutput.GetLastReplayPath();

            // Retry a few times if path is not immediately available
            for (int i = 0; i < 10 && string.IsNullOrEmpty(savedPath); i++)
            {
                savedPath = _bufferOutput.GetLastReplayPath();
                if (string.IsNullOrEmpty(savedPath))
                    await Task.Delay(100);
            }

            if (string.IsNullOrEmpty(savedPath))
            {
                Log.Error("Replay buffer path is null or empty");
                return false;
            }

            Log.Information($"Replay buffer saved to: {savedPath}");
            string game = Settings.Instance.State.Recording?.Game ?? "Unknown";
            string? exePath = Settings.Instance.State.Recording?.ExePath;
            int? igdbId = !string.IsNullOrEmpty(exePath) ? GameUtils.GetIgdbIdFromExePath(exePath) : null;

            // Ensure file is fully written to disk/network before thumbnail generation
            await EnsureFileReady(savedPath);

            // Create metadata for the buffer recording
            await ContentService.CreateMetadataFile(savedPath, Content.ContentType.Buffer, game, igdbId: igdbId);
            await ContentService.CreateThumbnail(savedPath, Content.ContentType.Buffer);
            await ContentService.CreateWaveformFile(savedPath, Content.ContentType.Buffer);

            // Reload content list to include the new buffer file
            await SettingsService.LoadContentFromFolderIntoState(true);

            Log.Information("Replay buffer save process completed successfully");

            // Restart replay buffer so subsequent saves only include new footage
            await ResetReplayBuffer();

            // Reset the flag
            _replaySaved = false;

            return true;
        }

        /// <summary>
        /// Stops and restarts the replay buffer so that subsequent saves
        /// only contain footage recorded after the last save.
        /// </summary>
        private static async Task ResetReplayBuffer()
        {
            if (_bufferOutput == null)
                return;

            Log.Information("Resetting replay buffer...");

            bool stopped = _bufferOutput.Stop(waitForCompletion: true, timeoutMs: 30000);

            if (!stopped)
            {
                Log.Warning("Replay buffer did not stop within timeout for reset. Forcing stop.");
                _bufferOutput.ForceStop();
                await Task.Delay(500);
            }

            bool started = _bufferOutput.Start();

            if (!started)
            {
                string error = _bufferOutput.LastError ?? "Unknown error";
                Log.Error($"Failed to restart replay buffer after reset: {error}");
            }
            else
            {
                Log.Information("Replay buffer restarted successfully");
            }
        }

        /// <summary>
        /// Processes OBS log messages from the queue asynchronously.
        /// This runs on a background thread to prevent blocking OBS's internal logging thread.
        /// </summary>
        private static async Task ProcessLogQueueAsync()
        {
            await foreach (var (level, formattedMessage) in _logChannel.Reader.ReadAllAsync())
            {
                try
                {
                    Log.Information($"{(ObsLogLevel)level}: {formattedMessage}");

                    if (formattedMessage.Contains("capture window no longer exists, terminating capture"))
                    {
                        // Some games will show the "capture window no longer exists" message when they are still running, so we wait a second to make sure it's not a false positive
                        Log.Information("Capture window no longer exists, waiting a second to make sure it's not a false positive.");
                        await Task.Delay(1000);
                        Log.Information("Checking if hook is still active: {_isStillHookedAfterUnhook}", _isStillHookedAfterUnhook);

                        // Check if any output is still active
                        if ((_output != null || _bufferOutput != null) && !_isStillHookedAfterUnhook)
                        {
                            Log.Information("Capture stopped. Stopping recording.");
                            _ = Task.Run(StopRecording);
                        }
                        _isStillHookedAfterUnhook = false;
                    }

                    // This means the game is still running after unhooking. We need this to prevent the method above to accidentally stop the recording.
                    if (formattedMessage.Contains("existing hook found"))
                    {
                        _isStillHookedAfterUnhook = true;
                    }

                    // Parse window dimensions from OBS game capture logs
                    if (formattedMessage.Contains("BufferDesc.Width:"))
                    {
                        var match = BufferDescWidthRegex().Match(formattedMessage);
                        if (match.Success && uint.TryParse(match.Groups[1].Value, out uint width))
                        {
                            CapturedWindowWidth = width;
                            Log.Information($"Captured window width: {width}");
                        }
                    }

                    if (formattedMessage.Contains("BufferDesc.Height:"))
                    {
                        var match = BufferDescHeightRegex().Match(formattedMessage);
                        if (match.Success && uint.TryParse(match.Groups[1].Value, out uint height))
                        {
                            CapturedWindowHeight = height;
                            Log.Information($"Captured window height: {height}");
                        }
                    }

                }
                catch (Exception e)
                {
                    Log.Error(e.ToString());
                    if (e.StackTrace != null)
                    {
                        Log.Error(e.StackTrace);
                    }
                }
            }
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
                await MessageService.ShowModal(
                    "Recorder Error",
                    "The recorder installation failed. Please check your internet connection and try again. If you have any games running, please close them and restart Segra.",
                    "error",
                    "Could not install recorder"
                );
                Settings.Instance.State.HasLoadedObs = true;
                return;
            }

            if (Obs.IsInitialized)
                throw new Exception("Error: OBS is already initialized.");

            // Start the log queue processor before setting the log handler
            _ = Task.Run(ProcessLogQueueAsync);

            try
            {
                // Initialize OBS using ObsKit.NET fluent API
                _obsContext = Obs.Initialize(config =>
                {
                    config
                        .WithLocale("en-US")
                        .WithDataPath("./data/libobs/")
                        .WithModulePath("./obs-plugins/64bit/", "./data/obs-plugins/%module%/")
                        .WithVideo(v => v
                            .Resolution(1920, 1080)
                            .Fps(60))
                        .WithAudio(a => a
                            .WithSampleRate(44100)
                            .WithSpeakers(SpeakerLayout.Stereo))
                        .WithLogging((level, message) =>
                        {
                            try
                            {
                                // Queue the message for async processing - this is non-blocking
                                _logChannel.Writer.TryWrite(((int)level, message));
                            }
                            catch
                            {
                                // Silently ignore marshaling errors to never block OBS
                            }
                        });
                });

                // Disable auto-dispose for manual resource management
                Obs.AutoDispose = false;

                InstalledOBSVersion = Obs.Version;
                Log.Information("OBS version: " + InstalledOBSVersion);

                // Set available encoders in state
                SetAvailableEncodersInState();

                IsInitialized = true;
                Settings.Instance.State.HasLoadedObs = true;
                Log.Information("OBS initialized successfully!");

                _ = Task.Run(RecoveryService.CheckForOrphanedFilesAsync);
                GameDetectionService.StartAsync();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to initialize OBS: {ex.Message}");
                await MessageService.ShowModal(
                    "Recorder Error",
                    "Failed to initialize the recorder. Please check the logs for more details.",
                    "error",
                    "Could not initialize recorder"
                );
                Settings.Instance.State.HasLoadedObs = true;
            }
        }

        public static void Shutdown()
        {
            if (!IsInitialized)
            {
                Log.Information("OBS is not initialized, skipping shutdown");
                return;
            }

            try
            {
                Log.Information("Shutting down OBS...");

                // Dispose the OBS context to properly clean up OBS resources
                _obsContext?.Dispose();
                _obsContext = null;

                IsInitialized = false;
                Log.Information("OBS shutdown completed successfully");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during OBS shutdown");
            }
        }

        /// <summary>
        /// Configures OBS video settings based on the provided dimensions.
        /// </summary>
        /// <param name="is4by3">True if the content was detected as 4:3 and stretched to 16:9.</param>
        private static void ResetVideoSettings(out bool is4by3, uint? customFps = null, uint? customOutputWidth = null, uint? customOutputHeight = null)
        {
            SettingsService.GetPrimaryMonitorResolution(out uint baseWidth, out uint baseHeight);

            // Use custom values if provided, otherwise use defaults
            baseWidth = customOutputWidth ?? baseWidth;
            baseHeight = customOutputHeight ?? baseHeight;

            // Get the maximum height from resolution setting
            SettingsService.GetResolution(Settings.Instance.Resolution, out uint maxWidth, out uint maxHeight);

            // Calculate output dimensions respecting the max height cap while preserving aspect ratio
            uint outputWidth = baseWidth;
            uint outputHeight = baseHeight;

            // Check if the input aspect ratio is close to 4:3 (1.33)
            double aspectRatio = (double)baseWidth / baseHeight;
            is4by3 = Math.Abs(aspectRatio - 4.0 / 3.0) < 0.1 && Settings.Instance.Stretch4By3;

            // If the content is 4:3 and stretching is enabled, stretch it to 16:9 while preserving height
            // Only modify output dimensions, not base dimensions (base = actual capture size)
            if (is4by3)
            {
                // Calculate 16:9 width based on the current height for output only
                outputWidth = (uint)(baseHeight * (16.0 / 9.0));
                Log.Information($"Stretching 4:3 content to 16:9: {baseWidth}x{baseHeight} -> {outputWidth}x{outputHeight}");
            }

            // If content height exceeds max height setting, downscale proportionally
            if (outputHeight > maxHeight)
            {
                double scale = (double)maxHeight / outputHeight;
                outputWidth = (uint)(outputWidth * scale);
                outputHeight = maxHeight;

                // Round to nearest multiple of 4 (required by video encoders)
                // Example: 1279 → 1280 instead of OBS rounding down to 1276
                outputWidth = (uint)(Math.Round(outputWidth / 4.0) * 4);
                outputHeight = (uint)(Math.Round(outputHeight / 4.0) * 4);

                Log.Information($"Downscaling from {baseWidth}x{baseHeight} to {outputWidth}x{outputHeight} (max height: {maxHeight})");
            }

            _currentBaseWidth = baseWidth;
            _currentBaseHeight = baseHeight;

            Obs.SetVideo(v => v
                .BaseResolution(baseWidth, baseHeight)
                .OutputResolution(outputWidth, outputHeight)
                .Fps(customFps ?? 60));
        }

        public static bool StartRecording(string name = "Manual Recording", string exePath = "Unknown", bool startManually = false, int? pid = null)
        {
            // Wait for pending StopRecording to complete before starting. Prevents race conditions where a new recording starts before cleanup finishes
            _stopRecordingSemaphore.Wait();
            _stopRecordingSemaphore.Release();

            if (!IsOBSInstalled())
            {
                Log.Information("OBS is not installed. Skipping recording.");
                return false;
            }

            if (!IsInitialized)
            {
                Log.Information("OBS is not initialized. Skipping recording.");
                return false;
            }

            bool isReplayBufferMode = Settings.Instance.RecordingMode == RecordingMode.Buffer;
            bool isSessionMode = Settings.Instance.RecordingMode == RecordingMode.Session;
            bool isHybridMode = Settings.Instance.RecordingMode == RecordingMode.Hybrid;

            string fileName = Path.GetFileName(exePath);

            // Prevent starting if any output is already active
            if (_bufferOutput != null || _output != null)
            {
                Log.Information("A recording or replay buffer is already in progress.");
                Settings.Instance.State.PreRecording = null;
                return false;
            }

            // Reset the stopping flag when starting a new recording
            _isStoppingOrStopped = false;

            // Configure video settings specifically for this recording/buffer
            ResetVideoSettings(out _, customFps: (uint)Settings.Instance.FrameRate);

            // Create main scene for this recording
            _mainScene = new Scene("Recording Scene");
            Log.Information("Created recording scene");

            // For manual recording, use display capture directly without game hooking
            if (startManually)
            {
                Log.Information("Manual recording started - using display capture");
                AddMonitorCapture();
                // Use base dimensions for bounds - scene canvas is at base resolution
                _displayItem?.SetBounds(ObsBoundsType.ScaleInner, _currentBaseWidth, _currentBaseHeight).SetPosition(0, 0);
            }
            else
            {
                // Add display capture first (bottom layer - fallback)
                AddMonitorCapture();

                // Create game capture source for automatic game detection
                try
                {
                    GameCaptureSource = new GameCapture("gameplay", GameCapture.CaptureMode.SpecificWindow);
                    GameCaptureSource.SetWindow($"*:*:{fileName}");

                    Log.Information($"Game capture configured for: {fileName}");

                    // Add game capture to scene (top layer - visible when hooked)
                    _gameCaptureItem = _mainScene.AddSource(GameCaptureSource);

                    // Start a timer to check if game capture hooks within 90 seconds
                    StartGameCaptureHookTimeoutTimer();

                    // Subscribe to GameCapture's hooked/unhooked events (IsHooked is tracked automatically)
                    GameCaptureSource!.Hooked += OnGameCaptureHookedEvent;
                    GameCaptureSource.Unhooked += OnGameCaptureUnhookedEvent;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Game Capture source not available: {ex.Message}. Using Display Capture only.");
                    GameCaptureSource = null;
                }

                // Try to get the window dimensions for the game
                if (WindowUtils.GetWindowDimensionsByPreRecordingExeOrPid(out uint windowWidth, out uint windowHeight))
                {
                    ResetVideoSettings(
                        out bool is4by3,
                        customFps: (uint)Settings.Instance.FrameRate,
                        customOutputWidth: windowWidth,
                        customOutputHeight: windowHeight
                    );

                    // Scene item bounds must use BASE dimensions (not output) because the scene canvas is at base resolution.
                    // For 4:3 content: base is 4:3, output is 16:9 - OBS handles the stretch at the output level.
                    // For non-4:3: base == output, ScaleInner ensures content scales with black bars if window shrinks.
                    var boundsType = is4by3 ? ObsBoundsType.Stretch : ObsBoundsType.ScaleInner;
                    _gameCaptureItem?.SetBounds(boundsType, _currentBaseWidth, _currentBaseHeight).SetPosition(0, 0);
                    _displayItem?.SetBounds(boundsType, _currentBaseWidth, _currentBaseHeight).SetPosition(0, 0);
                }
                else
                {
                    _ = Task.Run(StopRecording);
                    return false;
                }
            }

            // Set scene as program output
            _mainScene.SetAsProgram();

            // Create video encoder
            string encoderId = Settings.Instance.Codec!.InternalEncoderId;
            Log.Information($"Using encoder: {Settings.Instance.Codec!.FriendlyName} ({encoderId})");

            using var videoEncoderSettings = new ObsKit.NET.Core.Settings();
            videoEncoderSettings.Set("preset", "Quality");
            videoEncoderSettings.Set("profile", "high");
            videoEncoderSettings.Set("use_bufsize", true);
            videoEncoderSettings.Set("rate_control", Settings.Instance.RateControl);
            videoEncoderSettings.Set("keyint_sec", 1);

            switch (Settings.Instance.RateControl)
            {
                case "CBR":
                    int targetBitrateKbps = Settings.Instance.Bitrate * 1000;
                    videoEncoderSettings.Set("bitrate", targetBitrateKbps);
                    videoEncoderSettings.Set("max_bitrate", targetBitrateKbps);
                    videoEncoderSettings.Set("bufsize", targetBitrateKbps);
                    break;

                case "VBR":
                    int minBitrateKbps = Settings.Instance.MinBitrate * 1000;
                    int maxBitrateKbps = Settings.Instance.MaxBitrate * 1000;
                    videoEncoderSettings.Set("bitrate", minBitrateKbps);
                    videoEncoderSettings.Set("max_bitrate", maxBitrateKbps);
                    videoEncoderSettings.Set("bufsize", maxBitrateKbps);
                    break;

                case "CRF":
                    // Software x264 path mainly; no explicit bitrate
                    videoEncoderSettings.Set("crf", Settings.Instance.CrfValue);
                    break;

                case "CQP":
                    // Hardware encoders (NVENC/QSV/AMF) often use cqp/cq; provide both cqp and qp for compatibility
                    videoEncoderSettings.Set("cqp", Settings.Instance.CqLevel);
                    videoEncoderSettings.Set("qp", Settings.Instance.CqLevel);
                    break;

                default:
                    Settings.Instance.State.PreRecording = null;
                    throw new Exception("Unsupported Rate Control method.");
            }

            _videoEncoder = new VideoEncoder(encoderId, "Segra Recorder", videoEncoderSettings);

            // Create audio sources and add to scene
            if (Settings.Instance.InputDevices != null && Settings.Instance.InputDevices.Count > 0)
            {
                foreach (var deviceSetting in Settings.Instance.InputDevices)
                {
                    if (!string.IsNullOrEmpty(deviceSetting.Id))
                    {
                        string sourceName = $"Microphone_{_micSources.Count + 1}";
                        var micSource = AudioInputCapture.FromDevice(deviceSetting.Id, sourceName);

                        // Apply Force Mono if enabled
                        SetForceMono(micSource, Settings.Instance.ForceMonoInputSources);

                        micSource.Volume = deviceSetting.Volume;

                        _mainScene!.AddSource(micSource);
                        _micSources.Add(micSource);

                        Log.Information($"Added input device: {deviceSetting.Id} as {sourceName} with volume {deviceSetting.Volume}");
                    }
                }
            }

            if (Settings.Instance.OutputDevices != null && Settings.Instance.OutputDevices.Count > 0)
            {
                foreach (var deviceSetting in Settings.Instance.OutputDevices)
                {
                    if (!string.IsNullOrEmpty(deviceSetting.Id))
                    {
                        string sourceName = $"DesktopAudio_{_desktopSources.Count + 1}";
                        var desktopSource = AudioOutputCapture.FromDevice(deviceSetting.Id, sourceName);

                        desktopSource.Volume = deviceSetting.Volume;

                        _mainScene!.AddSource(desktopSource);
                        _desktopSources.Add(desktopSource);

                        Log.Information($"Added output device: {deviceSetting.Name} ({deviceSetting.Id}) as {sourceName} with volume {deviceSetting.Volume}");
                    }
                }
            }

            // Configure mixers and audio encoders based on setting.
            // If enabled: Track 1 = Full Mix, Tracks 2..6 = per-source isolated (up to 5 sources)
            // If disabled: Track 1 only (Full Mix)
            var allAudioSources = new List<Source>();
            allAudioSources.AddRange(_micSources);
            allAudioSources.AddRange(_desktopSources);

            // Build list of device names for encoder naming
            var audioDeviceNames = new List<string>();
            if (Settings.Instance.InputDevices != null)
            {
                foreach (var device in Settings.Instance.InputDevices.Where(d => !string.IsNullOrEmpty(d.Id)))
                    audioDeviceNames.Add(device.Name.Replace(" (Default)", "") ?? "Microphone");
            }
            if (Settings.Instance.OutputDevices != null)
            {
                foreach (var device in Settings.Instance.OutputDevices.Where(d => !string.IsNullOrEmpty(d.Id)))
                    audioDeviceNames.Add(device.Name.Replace(" (Default)", "") ?? "Desktop Audio");
            }

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
                    allAudioSources[i].AudioMixers = mixersMask;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to set mixers for audio source {i}: {ex.Message}");
                }
            }

            // Create one audio encoder per track and bind to corresponding mixer index
            _audioEncoders.Clear();
            for (int t = 0; t < trackCount; t++)
            {
                // Track 0 is the full mix, tracks 1+ are individual devices
                string encoderName = t == 0
                    ? "Full Mix"
                    : (t - 1 < audioDeviceNames.Count ? audioDeviceNames[t - 1] : $"Audio Track {t + 1}");

                var audioEncoder = AudioEncoder.CreateAac(encoderName, 128, t);
                _audioEncoders.Add(audioEncoder);
            }

            // Paths for session recordings and buffer, organized by game
            string sanitizedGameName = StorageService.SanitizeGameNameForFolder(name);
            string sessionDir = Path.Combine(Settings.Instance.ContentFolder, FolderNames.Sessions, sanitizedGameName);
            string bufferDir = Path.Combine(Settings.Instance.ContentFolder, FolderNames.Buffers, sanitizedGameName);
            if (!Directory.Exists(sessionDir)) Directory.CreateDirectory(sessionDir);
            if (!Directory.Exists(bufferDir)) Directory.CreateDirectory(bufferDir);

            string? videoOutputPath = null; // only set for session/hybrid session output

            // Configure outputs depending on mode
            if (isReplayBufferMode || isHybridMode)
            {
                uint bufferTracksMask = trackCount == 0 ? 0u : (1u << trackCount) - 1u;

                _bufferOutput = new ReplayBuffer("replay_buffer_output", Settings.Instance.ReplayBufferDuration, Settings.Instance.ReplayBufferMaxSize);
                _bufferOutput.SetDirectory(bufferDir);
                _bufferOutput.SetFilenameFormat("%CCYY-%MM-%DD_%hh-%mm-%ss");
                _bufferOutput.Update(s => s.Set("extension", "mp4").Set("tracks", (long)bufferTracksMask));

                // Set encoders
                _bufferOutput.WithVideoEncoder(_videoEncoder);
                for (int t = 0; t < _audioEncoders.Count; t++)
                {
                    _bufferOutput.WithAudioEncoder(_audioEncoders[t], track: t);
                }

                // Connect signal handler for replay saved
                _replaySavedConnection = _bufferOutput!.ConnectSignal(OutputSignal.Saved, OnReplaySaved);
            }

            if (isSessionMode || isHybridMode)
            {
                videoOutputPath = $"{sessionDir}/{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";

                uint recordTracksMask = trackCount == 0 ? 0u : (1u << trackCount) - 1u;

                bool useHybridMp4 = SupportsHybridMp4();
                Log.Information($"Using recording output type: {(useHybridMp4 ? "mp4_output" : "ffmpeg_muxer")} (Hybrid MP4: {useHybridMp4})");

                if (useHybridMp4)
                {
                    _output = new RecordingOutput("simple_output", videoOutputPath);
                    _output.SetFormat(RecordingFormat.HybridMp4);
                }
                else
                {
                    _output = new RecordingOutput("simple_output", videoOutputPath, "mp4");
                }
                _output.Update(s => s.Set("tracks", (long)recordTracksMask));

                // Set encoders
                _output.WithVideoEncoder(_videoEncoder);
                for (int t = 0; t < _audioEncoders.Count; t++)
                {
                    _output.WithAudioEncoder(_audioEncoders[t], track: t);
                }
            }

            // Overwrite the file name with the hooked executable name if using game hook
            fileName = _hookedExecutableFileName ?? fileName;

            DateTime? startTime = null;
            bool hasPlayedStartSound = false;

            if (_output != null)
            {
                if (!_output.Start())
                {
                    string error = _output.LastError ?? "Unknown error";
                    Log.Error($"Failed to start recording: {error}");
                    Task.Run(() => ShowModal("Recording failed", "Failed to start recording. Check the log for more details.", "error"));
                    Task.Run(() => PlaySound("error", 500));
                    Settings.Instance.State.PreRecording = null;
                    _ = Task.Run(StopRecording);
                    return false;
                }

                // Set the exact start time for session recording (Full Session has bookmarks)
                startTime = DateTime.Now;
                _ = Task.Run(() => PlaySound("start"));
                hasPlayedStartSound = true;

                Log.Information("Session recording started successfully");
            }

            if (_bufferOutput != null)
            {
                if (!_bufferOutput.Start())
                {
                    string error = _bufferOutput.LastError ?? "Unknown error";
                    Log.Error($"Failed to start replay buffer: {error}");
                    Task.Run(() => ShowModal("Replay buffer failed", "Failed to start replay buffer. Check the log for more details.", "error"));
                    Task.Run(() => PlaySound("error", 500));
                    Settings.Instance.State.PreRecording = null;
                    _ = Task.Run(StopRecording);
                    return false;
                }

                if (!hasPlayedStartSound)
                {
                    _ = Task.Run(() => PlaySound("start"));
                    hasPlayedStartSound = true;
                }

                Log.Information("Replay buffer started successfully");
            }

            string? gameImage = GameIconUtils.ExtractIconAsBase64(exePath);

            Settings.Instance.State.Recording = new Recording()
            {
                StartTime = startTime ?? DateTime.Now,
                Game = name,
                FilePath = videoOutputPath,
                FileName = fileName,
                Pid = pid,
                IsUsingGameHook = IsGameCaptureHooked,
                GameImage = gameImage,
                ExePath = exePath,
                CoverImageId = GameUtils.GetCoverImageIdFromExePath(exePath)
            };
            Settings.Instance.State.PreRecording = null;
            _ = MessageService.SendSettingsToFrontend("OBS Start recording");

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
            if (_mainScene == null)
            {
                Log.Warning("Cannot add monitor capture: scene not created");
                return;
            }

            int monitorIndex = 0;

            if (Settings.Instance.SelectedDisplay != null)
            {
                int? foundIndex = Settings.Instance.State.Displays
                    .Select((d, i) => new { Display = d, Index = i })
                    .Where(x => x.Display.DeviceId == Settings.Instance.SelectedDisplay?.DeviceId)
                    .Select(x => (int?)x.Index)
                    .FirstOrDefault();

                if (foundIndex.HasValue)
                {
                    monitorIndex = foundIndex.Value;
                }
                else
                {
                    _ = MessageService.ShowModal("Display recording", $"Could not find selected display. Defaulting to first automatically detected display.", "warning");
                }
            }

            var captureMethod = Settings.Instance.DisplayCaptureMethod switch
            {
                DisplayCaptureMethod.DXGI => MonitorCaptureMethod.DesktopDuplication,
                DisplayCaptureMethod.WGC => MonitorCaptureMethod.WindowsGraphicsCapture,
                _ => MonitorCaptureMethod.Auto
            };

            _displaySource = MonitorCapture.FromMonitor(monitorIndex, "display")
                .SetCaptureMethod(captureMethod);

            // Add to scene (display is behind game capture in layer order)
            _displayItem = _mainScene.AddSource(_displaySource);

            Log.Information($"Display capture added for monitor {monitorIndex} using {Settings.Instance.DisplayCaptureMethod} method");
        }

        public static async Task StopRecording()
        {
            // Prevent race conditions when multiple callers try to stop recording simultaneously
            await _stopRecordingSemaphore.WaitAsync();
            try
            {
                // Check if already stopping or stopped
                if (_isStoppingOrStopped)
                {
                    Log.Information("StopRecording called but already stopping or stopped.");
                    return;
                }

                // Mark as stopping to prevent concurrent stop attempts
                _isStoppingOrStopped = true;

                StopGameCaptureHookTimeoutTimer();

                bool isReplayBufferMode = Settings.Instance.RecordingMode == RecordingMode.Buffer;
                bool isHybridMode = Settings.Instance.RecordingMode == RecordingMode.Hybrid;

                if (isReplayBufferMode && _bufferOutput != null)
                {
                    // Stop replay buffer
                    Log.Information("Stopping replay buffer...");
                    bool successfullyStopped = _bufferOutput.Stop(waitForCompletion: true, timeoutMs: 30000);

                    if (successfullyStopped)
                    {
                        Log.Information("Replay buffer stopped.");
                        // Small delay just to be sure
                        Thread.Sleep(200);
                    }
                    else
                    {
                        Log.Warning("Replay buffer did not stop within timeout. Forcing stop.");
                        _bufferOutput.ForceStop();
                        Thread.Sleep(500); // Brief wait after force stop
                    }

                    DisposeOutput();
                    DisposeSources();
                    DisposeEncoders();

                    Log.Information("Replay buffer stopped and disposed.");

                    _ = GameIntegrationService.Shutdown();
                    KeybindCaptureService.Stop();

                    // Reload content list
                    await SettingsService.LoadContentFromFolderIntoState(false);
                }
                else if (!isReplayBufferMode && !isHybridMode && _output != null)
                {
                    // Stop standard recording
                    if (Settings.Instance.State.Recording != null)
                        Settings.Instance.State.UpdateRecordingEndTime(DateTime.Now);

                    Log.Information("Stopping recording...");
                    bool successfullyStopped = _output.Stop(waitForCompletion: true, timeoutMs: 30000);

                    if (successfullyStopped)
                    {
                        Log.Information("Recording stopped.");
                        // Small delay just to be sure
                        Thread.Sleep(200);
                    }
                    else
                    {
                        Log.Warning("Recording did not stop within timeout. Forcing stop.");
                        _output.ForceStop();
                        Thread.Sleep(500); // Brief wait after force stop
                    }

                    DisposeOutput();
                    DisposeSources();
                    DisposeEncoders();

                    Log.Information("Recording stopped and disposed.");

                    _ = GameIntegrationService.Shutdown();
                    KeybindCaptureService.Stop();

                    // Might be null or empty if the recording failed to start
                    if (Settings.Instance.State.Recording != null && Settings.Instance.State.Recording.FilePath != null)
                    {
                        // Check if we should discard the session due to no manual bookmarks
                        bool hasManualBookmarks = Settings.Instance.State.Recording.Bookmarks.Any(b => b.Type == BookmarkType.Manual);
                        if (Settings.Instance.DiscardSessionsWithoutBookmarks && !hasManualBookmarks)
                        {
                            Log.Information("Discarding session recording without manual bookmarks");
                            try
                            {
                                if (File.Exists(Settings.Instance.State.Recording.FilePath))
                                {
                                    File.Delete(Settings.Instance.State.Recording.FilePath);
                                    Log.Information($"Deleted video file: {Settings.Instance.State.Recording.FilePath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"Failed to delete discarded session file: {ex.Message}");
                            }
                        }
                        else
                        {
                            // Ensure file is fully written to disk/network before thumbnail generation
                            await EnsureFileReady(Settings.Instance.State.Recording.FilePath!);

                            int? igdbId = !string.IsNullOrEmpty(Settings.Instance.State.Recording.ExePath)
                                ? GameUtils.GetIgdbIdFromExePath(Settings.Instance.State.Recording.ExePath)
                                : null;
                            await ContentService.CreateMetadataFile(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session, Settings.Instance.State.Recording.Game, Settings.Instance.State.Recording.Bookmarks, igdbId: igdbId);
                            await ContentService.CreateThumbnail(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session);
                            await ContentService.CreateWaveformFile(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session);

                            Log.Information($"Recording details:");
                            Log.Information($"Start Time: {Settings.Instance.State.Recording.StartTime}");
                            Log.Information($"End Time: {Settings.Instance.State.Recording.EndTime}");
                            Log.Information($"Duration: {Settings.Instance.State.Recording.Duration}");
                            Log.Information($"File Path: {Settings.Instance.State.Recording.FilePath}");
                        }
                    }

                    await SettingsService.LoadContentFromFolderIntoState(false);
                }
                else if (isHybridMode)
                {
                    if (Settings.Instance.State.Recording != null)
                        Settings.Instance.State.UpdateRecordingEndTime(DateTime.Now);

                    // Stop replay buffer first if running
                    if (_bufferOutput != null)
                    {
                        Log.Information("Hybrid: Stopping replay buffer...");
                        bool successfullyStopped = _bufferOutput.Stop(waitForCompletion: true, timeoutMs: 30000);

                        if (successfullyStopped)
                        {
                            Log.Information("Hybrid: Replay buffer stopped.");
                            // Small delay just to be sure
                            Thread.Sleep(200);
                        }
                        else
                        {
                            Log.Warning("Hybrid: Replay buffer did not stop within timeout. Forcing stop.");
                            _bufferOutput.ForceStop();
                            Thread.Sleep(500);
                        }
                    }

                    // Stop session recording
                    if (_output != null)
                    {
                        Log.Information("Hybrid: Stopping recording...");
                        bool successfullyStopped = _output.Stop(waitForCompletion: true, timeoutMs: 30000);

                        if (successfullyStopped)
                        {
                            Log.Information("Hybrid: Recording stopped.");
                            // Small delay just to be sure
                            Thread.Sleep(200);
                        }
                        else
                        {
                            Log.Warning("Hybrid: Recording did not stop within timeout. Forcing stop.");
                            _output.ForceStop();
                            Thread.Sleep(500);
                        }
                    }

                    DisposeOutput();
                    DisposeSources();
                    DisposeEncoders();

                    Log.Information("Hybrid: All outputs stopped and disposed.");

                    _ = GameIntegrationService.Shutdown();
                    KeybindCaptureService.Stop();

                    if (Settings.Instance.State.Recording != null && Settings.Instance.State.Recording.FilePath != null)
                    {
                        // Check if we should discard the session due to no manual bookmarks
                        bool hasManualBookmarks = Settings.Instance.State.Recording.Bookmarks.Any(b => b.Type == BookmarkType.Manual);
                        if (Settings.Instance.DiscardSessionsWithoutBookmarks && !hasManualBookmarks)
                        {
                            Log.Information("Hybrid: Discarding session recording without manual bookmarks");
                            try
                            {
                                if (File.Exists(Settings.Instance.State.Recording.FilePath))
                                {
                                    File.Delete(Settings.Instance.State.Recording.FilePath);
                                    Log.Information($"Deleted video file: {Settings.Instance.State.Recording.FilePath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"Failed to delete discarded session file: {ex.Message}");
                            }
                        }
                        else
                        {
                            // Ensure file is fully written to disk/network before thumbnail generation
                            await EnsureFileReady(Settings.Instance.State.Recording.FilePath!);

                            int? igdbId = !string.IsNullOrEmpty(Settings.Instance.State.Recording.ExePath)
                                ? GameUtils.GetIgdbIdFromExePath(Settings.Instance.State.Recording.ExePath)
                                : null;
                            await ContentService.CreateMetadataFile(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session, Settings.Instance.State.Recording.Game, Settings.Instance.State.Recording.Bookmarks, igdbId: igdbId);
                            await ContentService.CreateThumbnail(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session);
                            await ContentService.CreateWaveformFile(Settings.Instance.State.Recording.FilePath!, Content.ContentType.Session);
                        }
                    }

                    await SettingsService.LoadContentFromFolderIntoState(false);
                }
                else
                {
                    DisposeOutput();
                    DisposeSources();
                    DisposeEncoders();
                    Settings.Instance.State.Recording = null;
                    Settings.Instance.State.PreRecording = null;
                }

                await StorageService.EnsureStorageBelowLimit();

                // Reset hooked executable file name and captured dimensions
                _hookedExecutableFileName = null;
                CapturedWindowWidth = null;
                CapturedWindowHeight = null;

                // If the recording ends before it started, don't do anything
                if (Settings.Instance.State.Recording == null || (!isReplayBufferMode && Settings.Instance.State.Recording.FilePath == null))
                {
                    Settings.Instance.State.PreRecording = null;
                    return;
                }

                // Get the file path before nullifying the recording (FilePath is not null at this point because of the previous check)
                string filePath = Settings.Instance.State.Recording.FilePath!;

                // Get the bookmarks before nullifying the recording
                List<Bookmark> bookmarks = Settings.Instance.State.Recording.Bookmarks;

                // Reset the recording and pre-recording
                Settings.Instance.State.Recording = null;
                Settings.Instance.State.PreRecording = null;

                // If the recording is not a replay buffer recording, AI is enabled, user is authenticated, and auto generate highlights is enabled -> analyze the video!
                if (Settings.Instance.EnableAi && Settings.Instance.AutoGenerateHighlights && !isReplayBufferMode && bookmarks.Any(b => b.Type.IncludeInHighlight()))
                {
                    string fileName = Path.GetFileNameWithoutExtension(filePath);
                    _ = AiService.CreateHighlight(fileName);
                }
            }
            finally
            {
                _stopRecordingSemaphore.Release();
            }
        }

        /// <summary>
        /// Event handler for GameCapture.Hooked event.
        /// </summary>
        private static void OnGameCaptureHookedEvent(GameCapture capture)
        {
            try
            {
                // GameCapture now provides hooked info directly via its properties
                string? title = capture.HookedWindowTitle?.Trim();
                string? windowClass = capture.HookedWindowClass?.Trim();
                string? executable = capture.HookedExecutable?.Trim();

                // IsHooked is now managed by GameCapture automatically
                StopGameCaptureHookTimeoutTimer();

                Log.Information($"Game hooked: Title='{title}', Class='{windowClass}', Executable='{executable}'");

                // Remove display capture to save resources while game is hooked
                DisposeDisplaySource();

                if (Settings.Instance.State.Recording != null)
                {
                    Settings.Instance.State.Recording.IsUsingGameHook = true;
                    _ = SendSettingsToFrontend("Updated game hook");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing OnGameCaptureHookedEvent");
            }
        }


        /// <summary>
        /// Event handler for GameCapture.Unhooked event.
        /// </summary>
        private static void OnGameCaptureUnhookedEvent(GameCapture capture)
        {
            // IsHooked is now managed by GameCapture automatically
            Log.Information("Game unhooked.");
        }

        private static void OnReplaySaved(nint calldata)
        {
            _replaySaved = true;
            Log.Information("Replay buffer saved callback received");
        }

        private static void SetForceMono(Source source, bool forceMono)
        {
            try
            {
                uint flags = source.Flags;
                bool currentlyMono = (flags & OBS_SOURCE_FLAG_FORCE_MONO) != 0;
                if (forceMono && !currentlyMono)
                {
                    source.Flags = flags | OBS_SOURCE_FLAG_FORCE_MONO;
                }
                else if (!forceMono && currentlyMono)
                {
                    source.Flags = flags & ~OBS_SOURCE_FLAG_FORCE_MONO;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to set force mono on source: {ex.Message}");
            }
        }

        public static void DisposeSources()
        {
            // Dispose the scene (automatically clears output channel and disposes scene items)
            if (_mainScene != null)
            {
                try
                {
                    _mainScene.Dispose();
                    Log.Information("Scene disposed");
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to dispose scene: {ex.Message}");
                }
                _mainScene = null;
            }

            // Clear scene item references
            _gameCaptureItem = null;
            _displayItem = null;

            // Now dispose sources
            DisposeDisplaySource();
            DisposeGameCaptureSource();

            // Dispose mic sources
            foreach (var micSource in _micSources)
            {
                try
                {
                    micSource.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to dispose mic source: {ex.Message}");
                }
            }
            _micSources.Clear();

            // Dispose desktop audio sources
            foreach (var desktopSource in _desktopSources)
            {
                try
                {
                    desktopSource.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to dispose desktop source: {ex.Message}");
                }
            }
            _desktopSources.Clear();
        }

        public static void DisposeGameCaptureSource()
        {
            if (GameCaptureSource != null)
            {
                try
                {
                    // Unsubscribe from events
                    GameCaptureSource.Hooked -= OnGameCaptureHookedEvent;
                    GameCaptureSource.Unhooked -= OnGameCaptureUnhookedEvent;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to unsubscribe from game capture events: {ex.Message}");
                }

                try
                {
                    GameCaptureSource.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to dispose game capture source: {ex.Message}");
                }
                GameCaptureSource = null;
                _gameCaptureItem = null;
            }
            // Dispose the timer if it exists
            StopGameCaptureHookTimeoutTimer();
        }

        private static void StartGameCaptureHookTimeoutTimer()
        {
            // Dispose any existing timer first
            StopGameCaptureHookTimeoutTimer();

            // Create a new timer that checks after 90 seconds
            _gameCaptureHookTimeoutTimer = new System.Threading.Timer(
                CheckGameCaptureHookStatus,
                null,
                90000, // 90 seconds delay
                Timeout.Infinite // Don't repeat
            );

            Log.Information("Started game capture hook timer (90 seconds)");
        }

        private static void StopGameCaptureHookTimeoutTimer()
        {
            if (_gameCaptureHookTimeoutTimer != null)
            {
                _gameCaptureHookTimeoutTimer.Dispose();
                _gameCaptureHookTimeoutTimer = null;
                Log.Information("Stopped game capture hook timer");
            }
        }

        private static void CheckGameCaptureHookStatus(object? state)
        {
            // Check if game capture has hooked
            if (!IsGameCaptureHooked)
            {
                Log.Warning("Game capture did not hook within 90 seconds. Removing game capture source.");
                DisposeGameCaptureSource();
            }
            else
            {
                Log.Information("Game capture hook check completed. Hook status: {0}", IsGameCaptureHooked ? "Hooked" : "Not hooked");
                // Just stop the timer without disposing the game capture source if it's hooked
                StopGameCaptureHookTimeoutTimer();
            }
        }

        public static void DisposeDisplaySource()
        {
            if (_displaySource != null)
            {
                try
                {
                    _displaySource.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to dispose display source: {ex.Message}");
                }
                _displaySource = null;
                _displayItem = null;
            }
        }

        /// <summary>
        /// Clears encoder references. Encoders are auto-disposed by OBSKit.NET when outputs stop.
        /// </summary>
        public static void DisposeEncoders()
        {
            _videoEncoder = null;
            _audioEncoders.Clear();
        }

        /// <summary>
        /// Clears output references and signal connections. Outputs are auto-disposed by OBSKit.NET when Stop() is called.
        /// </summary>
        public static void DisposeOutput()
        {
            _replaySavedConnection?.Dispose();
            _replaySavedConnection = null;
            _output = null;
            _bufferOutput = null;
        }

        public static async Task AvailableOBSVersionsAsync()
        {
            try
            {
                string url = "https://segra.tv/api/obs/versions";
                List<Core.Models.OBSVersion>? response = null;
                using (HttpClient client = new())
                {
                    try
                    {
                        response = await client.GetFromJsonAsync<List<Core.Models.OBSVersion>>(url);
                        if (response != null)
                        {
                            Log.Information($"Available OBS versions: {string.Join(", ", response.Select(v => v.Version))}");
                        }
                        else
                        {
                            Log.Warning("Received null OBS versions list from API");
                            response = new List<Core.Models.OBSVersion>();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error parsing OBS versions from API: {ex.Message}");
                        response = new List<Core.Models.OBSVersion>();
                    }
                }

                // Filter versions based on current Segra version compatibility
                if (response != null && response.Count > 0)
                {
                    // Get the current Segra version
                    NuGet.Versioning.SemanticVersion currentVersion;
                    if (UpdateService.UpdateManager.CurrentVersion != null)
                    {
                        currentVersion = NuGet.Versioning.SemanticVersion.Parse(UpdateService.UpdateManager.CurrentVersion.ToString());
                    }
                    else
                    {
                        // Running in local development, use a high version to ensure we get the latest stable version
                        currentVersion = NuGet.Versioning.SemanticVersion.Parse("9.9.9");
                        Log.Warning("Could not get current version from UpdateManager, using default version for OBS compatibility check");
                    }

                    // Filter to only compatible versions
                    List<Core.Models.OBSVersion> compatibleVersions = response.Where(v =>
                    {
                        // SupportsFrom: null or empty means no lower limit
                        bool supportsFrom = string.IsNullOrEmpty(v.SupportsFrom) ||
                                          (NuGet.Versioning.SemanticVersion.TryParse(v.SupportsFrom, out var minVersion) &&
                                           currentVersion >= minVersion);

                        // SupportsTo: null or empty means no upper limit
                        bool supportsTo = v.SupportsTo == null ||
                                        string.IsNullOrEmpty(v.SupportsTo) ||
                                        (NuGet.Versioning.SemanticVersion.TryParse(v.SupportsTo, out var maxVersion) &&
                                         currentVersion <= maxVersion);

                        return supportsFrom && supportsTo;
                    }).ToList();

                    Log.Information($"Compatible OBS versions for Segra {currentVersion}: {string.Join(", ", compatibleVersions.Select(v => v.Version))}");
                    response = compatibleVersions;
                }

                SettingsService.SetAvailableOBSVersions(response ?? new List<Core.Models.OBSVersion>());
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to get available OBS versions: {ex.Message}");
            }
        }

        public static bool IsOBSInstalled()
        {
            string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "obs.dll");
            return File.Exists(dllPath);
        }

        public static async Task CheckIfExistsOrDownloadAsync(bool isUpdate = false)
        {
            Log.Information("Checking if OBS is installed");

            // Ensure we have the latest available versions
            await AvailableOBSVersionsAsync();

            if (isUpdate)
            {
                // We need to reinstall the Segra app to apply the update, because all OBS resources are placed in the app directory
                Settings.Instance.PendingOBSUpdate = true;
                SettingsService.SaveSettings();
                await UpdateService.ForceReinstallCurrentVersionAsync();
                await ShowModal("OBS Update", "Please restart Segra to apply the update.");
                return;
            }

            if (IsOBSInstalled() && !isUpdate && !Settings.Instance.PendingOBSUpdate)
            {
                Log.Information("OBS is installed");
                return;
            }

            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;

            // Store obs.zip and hash in AppData to preserve them across updates
            string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");
            Directory.CreateDirectory(appDataDir); // Ensure directory exists

            string zipPath = Path.Combine(appDataDir, "obs.zip");
            string localHashPath = Path.Combine(appDataDir, "obs.hash");
            bool needsDownload = true;

            // Determine which version to download
            string? selectedVersion = Settings.Instance.SelectedOBSVersion;
            Core.Models.OBSVersion? versionToDownload = null;

            // If a specific version is selected, try to find it
            if (!string.IsNullOrEmpty(selectedVersion))
            {
                versionToDownload = Settings.Instance.State.AvailableOBSVersions
                    .FirstOrDefault(v => v.Version == selectedVersion);

                if (versionToDownload == null)
                {
                    Log.Warning($"Selected OBS version {selectedVersion} not found in available versions. Using latest stable version.");
                }
            }

            // If no specific version was selected or found, use the latest non-beta version
            if (versionToDownload == null)
            {
                versionToDownload = Settings.Instance.State.AvailableOBSVersions
                    .Where(v => !v.IsBeta)
                    .OrderByDescending(v => v.Version)
                    .FirstOrDefault();

                Log.Information($"Using latest stable OBS version: {versionToDownload?.Version}");
            }

            // Download the selected or latest version
            if (versionToDownload != null)
            {
                Log.Information($"Using OBS version: {versionToDownload.Version}");
                string metadataUrl = versionToDownload.Url; // This is the GitHub metadata URL

                using (var httpClient = new HttpClient())
                {
                    // First, fetch the metadata from GitHub
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Segra");
                    httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3.json");

                    Log.Information($"Fetching metadata for OBS version {versionToDownload.Version} from {metadataUrl}");
                    var response = await httpClient.GetAsync(metadataUrl);

                    if (!response.IsSuccessStatusCode)
                    {
                        Log.Error($"Failed to fetch metadata from {metadataUrl}. Status: {response.StatusCode}");
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
                    string actualDownloadUrl = metadata.DownloadUrl;

                    // Check if we already have the file with the correct hash
                    if (!isUpdate && File.Exists(zipPath) && File.Exists(localHashPath))
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
                            needsDownload = true;
                        }
                    }

                    // If this is an update or we need to download, proceed with download
                    if (needsDownload)
                    {
                        Log.Information($"Downloading OBS version {versionToDownload.Version}");

                        httpClient.DefaultRequestHeaders.Clear();

                        // Download with progress reporting
                        using var downloadResponse = await httpClient.GetAsync(actualDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                        downloadResponse.EnsureSuccessStatusCode();

                        var totalBytes = downloadResponse.Content.Headers.ContentLength ?? -1L;
                        using var contentStream = await downloadResponse.Content.ReadAsStreamAsync();
                        using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                        var buffer = new byte[8192];
                        long totalBytesRead = 0;
                        int bytesRead;
                        int lastReportedProgress = -1;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalBytesRead += bytesRead;

                            if (totalBytes > 0)
                            {
                                int progress = (int)((totalBytesRead * 100) / totalBytes);
                                // Only send update if progress changed (avoid flooding)
                                if (progress != lastReportedProgress)
                                {
                                    lastReportedProgress = progress;
                                    await SendFrontendMessage("ObsDownloadProgress", new { progress, status = "downloading" });
                                }
                            }
                        }

                        // Save the hash for future reference
                        await File.WriteAllTextAsync(localHashPath, remoteHash);

                        Log.Information("Download complete");
                    }
                }

                // This should already be deleted on reinstall, but just in case
                if (Settings.Instance.PendingOBSUpdate)
                {
                    string dataPath = Path.Combine(currentDirectory, "data");
                    if (Directory.Exists(dataPath))
                    {
                        Directory.Delete(dataPath, true);
                    }

                    string obsPluginsPath = Path.Combine(currentDirectory, "obs-plugins");
                    if (Directory.Exists(obsPluginsPath))
                    {
                        Directory.Delete(obsPluginsPath, true);
                    }
                }

                try
                {
                    ZipFile.ExtractToDirectory(zipPath, currentDirectory, true);

                    if (Settings.Instance.PendingOBSUpdate)
                    {
                        await ShowModal("OBS Update", $"OBS update to {versionToDownload.Version} applied successfully.");
                        Settings.Instance.PendingOBSUpdate = false;
                        SettingsService.SaveSettings();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to extract OBS: {ex.Message}");
                    await ShowModal("OBS Update", "Failed to apply OBS update. Please try again.", "error");
                    throw;
                }

                Log.Information("OBS setup complete");
                return;
            }

            // If we somehow got here without a version to download, log an error
            Log.Error("No OBS versions available from API. This should not happen.");
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

            using var waveOut = new WaveOutEvent { DesiredLatency = 50 };
            waveOut.Init(volumeProvider);
            waveOut.Play();

            while (waveOut.PlaybackState == PlaybackState.Playing)
                Thread.Sleep(10);
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
                ["ffmpeg_openh264"] = "Software OpenH264",
            };

        private static void SetAvailableEncodersInState()
        {
            Log.Information("Available encoders:");

            // Enumerate all encoder types using ObsKit.NET
            var encoderTypes = Obs.EnumerateEncoderTypes().ToList();
            int idx = 0;

            foreach (var encoderId in encoderTypes)
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

        public static bool SupportsHybridMp4()
        {
            string? versionToCheck = Settings.Instance.SelectedOBSVersion ?? InstalledOBSVersion;

            if (string.IsNullOrEmpty(versionToCheck))
                return true;

            string cleanVersion = versionToCheck.Split('-')[0].Trim();
            if (Version.TryParse(cleanVersion, out Version? version))
                return version >= new Version(30, 2);

            return true;
        }
    }
}
