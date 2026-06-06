using NAudio.CoreAudioApi;
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
using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using static Segra.Backend.Shared.GeneralUtils;
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

        // OBS output stop codes (from libobs/obs-defs.h), passed as "code" in the output "stop" signal
        private const int OBS_OUTPUT_SUCCESS = 0;
        private const int OBS_OUTPUT_BAD_PATH = -1;
        private const int OBS_OUTPUT_CONNECT_FAILED = -2;
        private const int OBS_OUTPUT_INVALID_STREAM = -3;
        private const int OBS_OUTPUT_ERROR = -4;
        private const int OBS_OUTPUT_DISCONNECTED = -5;
        private const int OBS_OUTPUT_UNSUPPORTED = -6;
        private const int OBS_OUTPUT_NO_SPACE = -7;
        private const int OBS_OUTPUT_ENCODE_ERROR = -8;
        private const int OBS_OUTPUT_HDR_DISABLED = -9;

        // Regex patterns for buffer parsing
        [GeneratedRegex(@"BufferDesc\.Width:\s*(\d+)")]
        private static partial Regex BufferDescWidthRegex();

        [GeneratedRegex(@"BufferDesc\.Height:\s*(\d+)")]
        private static partial Regex BufferDescHeightRegex();

        // Public properties
        public static bool IsInitialized { get; private set; }
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
        private static Source? _discordAudioSource;

        // OBS encoder resources
        private static VideoEncoder? _videoEncoder;
        private static readonly List<AudioEncoder> _audioEncoders = [];

        // Game capture state
        private static string? _hookedExecutableFileName;
        private static System.Threading.Timer? _gameCaptureHookTimeoutTimer = null;
        private static bool _isStillHookedAfterUnhook = false;

        // Periodic low-disk-space monitor while recording
        private static System.Threading.Timer? _diskSpaceMonitorTimer = null;
        private const int DiskSpaceCheckIntervalMs = 60000; // 1 minute
        // Quality-based rate controls (CRF/CQP) have no bitrate cap, so assume a high worst case when sizing headroom
        private const int QualityModeAssumedMbps = 150;

        // Recording/output state
        private static bool _isStoppingOrStopped = false;
        private static uint _currentBaseWidth;
        private static uint _currentBaseHeight;
        private static uint _currentOutputWidth;
        private static uint _currentOutputHeight;

        // HDR state for the current recording. Decided once at StartRecording from the captured
        // display's HDR mode, because the OBS canvas color space cannot change while an output is
        // active. Both the display-capture fallback and the game capture inherit this canvas.
        private static bool _isHdrRecording = false;
        private static string? _hdrEncoderId = null;

        // When the connected displays disagree on HDR, an auto-started game's HDR decision depends on
        // which monitor the game opens on. The game's window doesn't exist the instant the process is
        // detected, so we wait for it before locking the canvas color space. StartRecording already
        // blocks waiting for the window to resolve capture dimensions, so this adds no real delay.
        private const int HdrWindowWaitAttempts = 120;
        private const int HdrWindowWaitDelayMs = 500; // ~60s, matching StartRecording's dimension-resolution wait

        // Encoders that can produce 10-bit HDR. H.264/AVC and x264 cannot encode HDR.
        private static readonly string[] HdrHevcEncoders = { "jim_hevc_nvenc", "obs_nvenc_hevc_tex", "h265_texture_amf", "obs_qsv11_hevc" };
        private static readonly string[] HdrAv1Encoders = { "jim_av1_nvenc", "obs_nvenc_av1_tex", "av1_texture_amf", "obs_qsv11_av1" };

        // Maps a user-selected H.264 encoder to the same vendor's HDR-capable encoders (HEVC then AV1).
        private static readonly Dictionary<string, string[]> HdrEncoderSubstitutes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jim_nvenc"] = new[] { "jim_hevc_nvenc", "jim_av1_nvenc" },
            ["obs_nvenc_h264_tex"] = new[] { "obs_nvenc_hevc_tex", "obs_nvenc_av1_tex", "jim_hevc_nvenc", "jim_av1_nvenc" },
            ["h264_texture_amf"] = new[] { "h265_texture_amf", "av1_texture_amf" },
            ["obs_qsv11_v2"] = new[] { "obs_qsv11_hevc", "obs_qsv11_av1" },
            ["obs_qsv11"] = new[] { "obs_qsv11_hevc", "obs_qsv11_av1" },
        };

        // Replay buffer state
        private static bool _replaySaved = false;

        // Signal connection for replay buffer saved event
        private static SignalConnection? _replaySavedConnection;

        // Signal connections for unexpected output stops (disk full, encoder errors, etc.)
        private static SignalConnection? _outputStoppedConnection;
        private static SignalConnection? _bufferStoppedConnection;

        // Ensures an unexpected stop is handled once even if multiple outputs stop together (e.g. hybrid mode)
        private static int _unexpectedStopHandled = 0;

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

            savedPath = PathUtils.Normalize(savedPath);
            Log.Information($"Replay buffer saved to: {savedPath}");
            string game = AppState.Instance.Recording?.Game ?? "Unknown";
            string? exePath = AppState.Instance.Recording?.ExePath;
            int? igdbId = !string.IsNullOrEmpty(exePath) ? GameUtils.GetIgdbIdFromExePath(exePath) : null;

            // Ensure file is fully written to disk/network before thumbnail generation
            await EnsureFileReady(savedPath);

            // Create metadata for the buffer recording
            await ContentService.CreateMetadataFile(savedPath, Content.ContentType.Buffer, game, igdbId: igdbId, audioTrackNames: AppState.Instance.Recording?.AudioTrackNames);
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
                AppState.Instance.HasLoadedObs = true;
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
                AppState.Instance.HasLoadedObs = true;
                Log.Information("OBS initialized successfully!");

                _ = Task.Run(RecoveryService.CheckForOrphanedFilesAsync);
                _ = GameDetectionService.StartAsync();
                GameDetectionService.ForegroundHook.Start();
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
                AppState.Instance.HasLoadedObs = true;
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
            _currentOutputWidth = outputWidth;
            _currentOutputHeight = outputHeight;

            // Must be set on every reset: OBSKit reuses its settings object, so a prior HDR
            // recording would otherwise leave the next SDR one in P010/PQ.
            Obs.SetVideo(v =>
            {
                v.BaseResolution(baseWidth, baseHeight)
                 .OutputResolution(outputWidth, outputHeight)
                 .Fps(customFps ?? 60);

                if (_isHdrRecording)
                    v.Hdr();
                else
                    v.Sdr();
            });
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
                AppState.Instance.PreRecording = null;
                return false;
            }

            // Prevent starting if any of the system, recording or temp drives are almost full
            List<StorageService.FullDrive> fullDrives = StorageService.GetFullDrives();
            if (fullDrives.Count > 0)
            {
                string drivesText = string.Join(", ", fullDrives.Select(d => $"{d.Label} ({d.Root.TrimEnd('\\')}) is {d.UsedPercent:F1}% full"));
                Log.Error($"Cannot start recording, drive(s) over {StorageService.DriveFullThresholdPercent:F0}% full: {drivesText}");
                // Stop the game detection polling loop from retrying until the user switches foreground window
                GameDetectionService.PreventRetryRecording = true;
                Task.Run(() => ShowModal("Not enough disk space", $"Recording cannot start because {drivesText}. Free up some space and try again.", "error"));
                Task.Run(() => PlaySound("error"));
                AppState.Instance.PreRecording = null;
                return false;
            }

            // Prevent starting if the recording drive does not have enough free space to record at the
            // configured bitrate (same threshold the in-recording monitor would immediately stop at).
            long? freeBytes = StorageService.GetContentDriveFreeBytes();
            long freeSpaceThreshold = GetRecordingFreeSpaceThresholdBytes();
            if (freeBytes != null && freeBytes.Value < freeSpaceThreshold)
            {
                double freeMb = freeBytes.Value / (1024.0 * 1024.0);
                long thresholdMb = freeSpaceThreshold / (1024 * 1024);
                Log.Error($"Cannot start recording, recording drive low on space ({freeMb:F0} MB free, need {thresholdMb} MB for the configured bitrate).");
                // Stop the game detection polling loop from retrying until the user switches foreground window
                GameDetectionService.PreventRetryRecording = true;
                Task.Run(() => ShowModal("Not enough disk space", $"Recording cannot start because the recording drive only has {freeMb:F0} MB free. Free up some space and try again.", "error"));
                Task.Run(() => PlaySound("error"));
                AppState.Instance.PreRecording = null;
                return false;
            }

            // Reset the stopping flag when starting a new recording
            _isStoppingOrStopped = false;
            _unexpectedStopHandled = 0;

            // Decide HDR up front (the canvas color space cannot change once recording starts) and
            // switch to an HDR-capable encoder when the captured display is in HDR mode.
            _isHdrRecording = false;
            _hdrEncoderId = null;
            try
            {
                // Base HDR on the monitor whose content we actually capture: for a game, the monitor
                // the game window is on (so a game on an SDR monitor is never forced to PQ); for a
                // manual recording, the selected display.
                string? hdrTargetDeviceId = startManually
                    ? GetCaptureTargetDeviceId()
                    : ResolveGameHdrTargetDeviceId();

                if (HdrDetectionService.IsDisplayHdrActive(hdrTargetDeviceId))
                {
                    string userEncoderId = Settings.Instance.Codec?.InternalEncoderId ?? string.Empty;
                    string? hdrEncoderId = ResolveHdrEncoder(userEncoderId, AppState.Instance.Codecs);
                    if (hdrEncoderId != null)
                    {
                        _isHdrRecording = true;
                        _hdrEncoderId = hdrEncoderId;
                        if (!string.Equals(hdrEncoderId, userEncoderId, StringComparison.OrdinalIgnoreCase))
                            Log.Information($"HDR display detected; using HDR-capable encoder '{hdrEncoderId}' instead of '{userEncoderId}'");
                        Log.Information("Recording in HDR (Rec.2100 PQ, 10-bit P010)");
                    }
                    else
                    {
                        Log.Warning("HDR display detected but no HDR-capable (HEVC/AV1) encoder is available; recording in SDR.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"HDR detection failed, recording in SDR: {ex.Message}");
                _isHdrRecording = false;
                _hdrEncoderId = null;
            }

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

                    // OBS can't auto-detect HDR game capture and defaults a 10-bit (R10G10B10A2)
                    // swapchain to sRGB, so an HDR game would be captured as SDR. Force Rec.2100 PQ.
                    if (_isHdrRecording)
                    {
                        GameCaptureSource.Update(s => s.Set("rgb10a2_space", "2100pq"));
                        Log.Information("Game capture color space set to Rec.2100 PQ (HDR)");
                    }

                    // Enable capture_audio on game capture when using GameOnly or GameAndDiscord mode
                    if (Settings.Instance.AudioOutputMode != AudioOutputMode.All)
                    {
                        GameCaptureSource.Update(s => s.Set("capture_audio", true));
                        Log.Information($"Game capture audio enabled (mode: {Settings.Instance.AudioOutputMode})");
                    }

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
            if (_isHdrRecording && _hdrEncoderId != null)
                encoderId = _hdrEncoderId;
            Log.Information($"Using encoder: {encoderId}{(_isHdrRecording ? " (HDR)" : "")}");

            using var videoEncoderSettings = new ObsKit.NET.Core.Settings();
            videoEncoderSettings.Set("preset", "Quality");
            // HEVC needs the Main 10 profile for 10-bit HDR; AV1 derives bit depth from the P010 input.
            videoEncoderSettings.Set("profile", _isHdrRecording && IsHevcEncoder(encoderId) ? "main10" : "high");
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
                    AppState.Instance.PreRecording = null;
                    throw new Exception("Unsupported Rate Control method.");
            }

            // Disable HEVC b-frames on older NVIDIA GPUs (requires compute capability >= 7.0)
            if (encoderId.Equals("jim_hevc_nvenc", StringComparison.OrdinalIgnoreCase) &&
                AppState.Instance.CudaComputeCapability != null &&
                AppState.Instance.CudaComputeCapability < 7.0)
            {
                videoEncoderSettings.Set("bf", 0);
                Log.Information("NVENC b-frames disabled (CUDA compute capability < 7.0)");
            }

            try
            {
                _videoEncoder = new VideoEncoder(encoderId, "Segra Recorder", videoEncoderSettings);
            }
            catch (Exception ex) when (_isHdrRecording)
            {
                // Some older GPUs expose an HEVC/AV1 encoder but cannot encode 10-bit; fall back to SDR.
                Log.Warning($"Failed to create HDR encoder '{encoderId}' ({ex.Message}); falling back to SDR.");
                _isHdrRecording = false;
                _hdrEncoderId = null;

                Obs.SetVideo(v => v
                    .BaseResolution(_currentBaseWidth, _currentBaseHeight)
                    .OutputResolution(_currentOutputWidth, _currentOutputHeight)
                    .Fps((uint)Settings.Instance.FrameRate)
                    .Sdr());

                encoderId = Settings.Instance.Codec!.InternalEncoderId;
                videoEncoderSettings.Set("profile", "high");
                _videoEncoder = new VideoEncoder(encoderId, "Segra Recorder", videoEncoderSettings);
            }

            // Create audio sources and add to scene
            if (Settings.Instance.InputDevices != null && Settings.Instance.InputDevices.Count > 0)
            {
                foreach (var deviceSetting in Settings.Instance.InputDevices)
                {
                    if (!string.IsNullOrEmpty(deviceSetting.Id))
                    {
                        string sourceName = $"Microphone_{_micSources.Count + 1}";
                        var micSource = deviceSetting.Id == "default"
                            ? AudioInputCapture.FromDefault(sourceName)
                            : AudioInputCapture.FromDevice(deviceSetting.Id, sourceName);

                        // Apply Force Mono if enabled
                        SetForceMono(micSource, Settings.Instance.ForceMonoInputSources);

                        micSource.Volume = deviceSetting.Volume;

                        _mainScene!.AddSource(micSource);
                        _micSources.Add(micSource);

                        if (Settings.Instance.InputNoiseSuppression)
                        {
                            try
                            {
                                var noiseGate = new Source("noise_gate_filter", $"{sourceName}_NoiseGate");
                                noiseGate.Update(s =>
                                {
                                    s.Set("close_threshold", -48.0);
                                    s.Set("open_threshold", -42.0);
                                    s.Set("attack_time", 25L);
                                    s.Set("hold_time", 200L);
                                    s.Set("release_time", 150L);
                                });
                                micSource.AddFilter(noiseGate);
                                Log.Information($"Added noise suppression filter to {sourceName}");
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"Failed to add noise suppression filter to {sourceName}: {ex.Message}");
                            }
                        }

                        Log.Information($"Added input device: {deviceSetting.Id} as {sourceName} with volume {deviceSetting.Volume}");
                    }
                }
            }

            var audioOutputMode = Settings.Instance.AudioOutputMode;

            // Always add desktop audio sources - they serve as fallback until game hooks in GameOnly/GameAndDiscord modes
            if (Settings.Instance.OutputDevices != null && Settings.Instance.OutputDevices.Count > 0)
            {
                foreach (var deviceSetting in Settings.Instance.OutputDevices)
                {
                    if (!string.IsNullOrEmpty(deviceSetting.Id))
                    {
                        string sourceName = $"DesktopAudio_{_desktopSources.Count + 1}";
                        var desktopSource = deviceSetting.Id == "default"
                            ? AudioOutputCapture.FromDefault(sourceName)
                            : AudioOutputCapture.FromDevice(deviceSetting.Id, sourceName);

                        desktopSource.Volume = deviceSetting.Volume;

                        _mainScene!.AddSource(desktopSource);
                        _desktopSources.Add(desktopSource);

                        Log.Information($"Added output device: {deviceSetting.Name} ({deviceSetting.Id}) as {sourceName} with volume {deviceSetting.Volume}");
                    }
                }
            }

            // In GameAndDiscord mode, also create Discord application audio capture (starts muted until game hooks)
            if (audioOutputMode == AudioOutputMode.GameAndDiscord && GameCaptureSource != null)
            {
                try
                {
                    _discordAudioSource = new Source("wasapi_process_output_capture", "Discord Audio");
                    _discordAudioSource.Update(s =>
                    {
                        // Window format: title:class:executable
                        s.Set("window", "Discord:Chrome_WidgetWin_1:Discord.exe");
                        s.Set("priority", 2); // WINDOW_PRIORITY_EXE
                    });
                    _discordAudioSource.IsMuted = true; // Muted until game hooks (desktop audio covers Discord until then)
                    _mainScene!.AddSource(_discordAudioSource);
                    Log.Information("Added Discord application audio capture source (muted until game hooks)");
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to create Discord audio capture source: {ex.Message}");
                    _discordAudioSource = null;
                }
            }

            // Configure mixers and audio encoders based on setting.
            // If enabled: Track 1 = Full Mix, Tracks 2..6 = per-source isolated (up to 5 sources)
            // If disabled: Track 1 only (Full Mix)
            // In GameOnly/GameAndDiscord modes, desktop sources are fallback-only (full mix only).
            var allAudioSources = new List<Source>();
            allAudioSources.AddRange(_micSources);
            allAudioSources.AddRange(_desktopSources);

            if (audioOutputMode != AudioOutputMode.All && GameCaptureSource != null)
            {
                // Desktop sources are fallback-only: assign to full mix (Track 1) only, no separate tracks
                foreach (var desktopSource in _desktopSources)
                {
                    try { desktopSource.AudioMixers = 1u << 0; }
                    catch (Exception ex) { Log.Warning($"Failed to set mixer for fallback desktop source: {ex.Message}"); }
                }

                // Remove desktop sources from the list that gets separate tracks
                allAudioSources = new List<Source>();
                allAudioSources.AddRange(_micSources);
                allAudioSources.Add(GameCaptureSource);
                if (audioOutputMode == AudioOutputMode.GameAndDiscord && _discordAudioSource != null)
                    allAudioSources.Add(_discordAudioSource);
            }

            // Build list of device names for encoder naming
            var audioDeviceNames = new List<string>();
            if (Settings.Instance.InputDevices != null)
            {
                foreach (var device in Settings.Instance.InputDevices.Where(d => !string.IsNullOrEmpty(d.Id)))
                    audioDeviceNames.Add(device.Name.Replace(" (Default)", "") ?? "Microphone");
            }
            if (audioOutputMode == AudioOutputMode.All || GameCaptureSource == null)
            {
                if (Settings.Instance.OutputDevices != null)
                {
                    foreach (var device in Settings.Instance.OutputDevices.Where(d => !string.IsNullOrEmpty(d.Id)))
                        audioDeviceNames.Add(device.Name.Replace(" (Default)", "") ?? "Desktop Audio");
                }
            }
            else
            {
                audioDeviceNames.Add("Game Audio");
                if (audioOutputMode == AudioOutputMode.GameAndDiscord && _discordAudioSource != null)
                    audioDeviceNames.Add("Discord");
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

            // Create one audio encoder per track and bind to corresponding mixer index.
            // Also capture the authoritative track name list so downstream code (metadata,
            // clip creation, UI) matches what OBS actually recorded.
            _audioEncoders.Clear();
            var actualAudioTrackNames = new List<string>(trackCount);
            for (int t = 0; t < trackCount; t++)
            {
                // Track 0 is the full mix, tracks 1+ are individual devices
                string encoderName = t == 0
                    ? "Full Mix"
                    : (t - 1 < audioDeviceNames.Count ? audioDeviceNames[t - 1] : $"Audio Track {t + 1}");

                actualAudioTrackNames.Add(encoderName);
                var audioEncoder = AudioEncoder.CreateAac(encoderName, 128, t);
                _audioEncoders.Add(audioEncoder);
            }

            // Paths for session recordings and buffer, organized by game
            string sanitizedGameName = StorageService.SanitizeGameNameForFolder(name);
            string sessionDir = PathUtils.Combine(Settings.Instance.ContentFolder, FolderNames.Sessions, sanitizedGameName);
            string bufferDir = PathUtils.Combine(Settings.Instance.ContentFolder, FolderNames.Buffers, sanitizedGameName);
            if (!Directory.Exists(sessionDir)) Directory.CreateDirectory(sessionDir);
            if (!Directory.Exists(bufferDir)) Directory.CreateDirectory(bufferDir);

            string? videoOutputPath = null; // only set for session/hybrid session output

            // Configure outputs depending on mode
            if (isReplayBufferMode || isHybridMode)
            {
                uint bufferTracksMask = (1u << trackCount) - 1u;

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

                // Detect unexpected stops (e.g. disk full mid-recording) so we can notify the user
                _bufferStoppedConnection = _bufferOutput!.ConnectSignal(OutputSignal.Stop, OnOutputStopped);
            }

            if (isSessionMode || isHybridMode)
            {
                videoOutputPath = $"{sessionDir}/{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";

                uint recordTracksMask = (1u << trackCount) - 1u;

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

                // Detect unexpected stops (e.g. disk full mid-recording) so we can notify the user
                _outputStoppedConnection = _output.ConnectSignal(OutputSignal.Stop, OnOutputStopped);
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
                    Task.Run(() => PlaySound("error"));
                    AppState.Instance.PreRecording = null;
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
                    Task.Run(() => PlaySound("error"));
                    AppState.Instance.PreRecording = null;
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

            AppState.Instance.Recording = new Recording()
            {
                StartTime = startTime ?? DateTime.Now,
                Game = name,
                FilePath = videoOutputPath,
                FileName = fileName,
                Pid = pid,
                IsUsingGameHook = IsGameCaptureHooked,
                ExePath = exePath,
                CoverImageId = GameUtils.GetCoverImageIdFromExePath(exePath),
                AudioTrackNames = actualAudioTrackNames
            };
            AppState.Instance.PreRecording = null;
            _ = MessageService.SendStateToFrontend("OBS Start recording");

            RecordingPreviewService.OnRecordingStarted((uint)Settings.Instance.FrameRate);

            NotifyIconService.SetNotifyIconStatus(NotifyIconState.Recording);

            StartDiskSpaceMonitor();

            Log.Information("Recording started: " + videoOutputPath);
            GeneralUtils.SetProcessPriority(ProcessPriorityClass.High);
            if (!isReplayBufferMode)
            {
                _ = GameIntegrationService.Start(GameUtils.GetIgdbIdFromExePath(exePath), GameUtils.GetGameNameFromExePath(exePath), exePath);
            }
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
                int? foundIndex = AppState.Instance.Displays
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

        /// <summary>
        /// Resolves the device id of the display that will be captured, mirroring the selection
        /// AddMonitorCapture makes (the selected display if found, otherwise the first display).
        /// Used to decide whether to record in HDR.
        /// </summary>
        private static string? GetCaptureTargetDeviceId()
        {
            var displays = AppState.Instance.Displays;
            if (displays == null || displays.Count == 0)
                return null;

            if (Settings.Instance.SelectedDisplay != null)
            {
                var match = displays.FirstOrDefault(d => d.DeviceId == Settings.Instance.SelectedDisplay!.DeviceId);
                if (match != null)
                    return match.DeviceId;
            }

            return displays[0].DeviceId;
        }

        /// <summary>
        /// Resolves the display whose HDR state should drive an auto-started game recording. Prefers
        /// the monitor the game window is on. Because the window doesn't exist the instant the process
        /// is detected, we wait (bounded) for it - but only when the connected displays disagree on
        /// HDR, since otherwise the game's monitor can't change the decision and waiting would delay
        /// the recording for nothing. Falls back to the captured display if the window never appears.
        /// </summary>
        private static string? ResolveGameHdrTargetDeviceId()
        {
            string? fallbackDeviceId = GetCaptureTargetDeviceId();

            // If every connected display shares the fallback's HDR state (single monitor / all-SDR /
            // all-HDR), which monitor the game opens on is irrelevant - decide now without waiting.
            bool needWindow = DisplaysDisagreeOnHdr(fallbackDeviceId);
            int attempts = needWindow ? HdrWindowWaitAttempts : 3;
            int delayMs = needWindow ? HdrWindowWaitDelayMs : 100;

            if (needWindow)
                Log.Information("HDR detection: displays disagree on HDR; waiting up to {TimeoutMs}ms for the game window to determine its monitor.", attempts * delayMs);

            string? windowDeviceId = DisplayService.GetDeviceIdForWindow(
                WindowUtils.TryGetPreRecordingWindowHandle(maxAttempts: attempts, delayMs: delayMs));

            if (windowDeviceId != null)
                return windowDeviceId;

            if (needWindow)
                Log.Information("HDR detection: game window not found within wait budget; using fallback display for the HDR decision.");
            return fallbackDeviceId;
        }

        /// <summary>
        /// True when the connected displays don't all share the fallback display's HDR state, meaning
        /// the monitor a game opens on actually changes whether the recording should be HDR.
        /// </summary>
        private static bool DisplaysDisagreeOnHdr(string? fallbackDeviceId)
        {
            var displays = AppState.Instance.Displays;
            if (displays == null || displays.Count < 2)
                return false;

            bool fallbackHdr = HdrDetectionService.IsDisplayHdrActive(fallbackDeviceId);
            return displays.Any(d => HdrDetectionService.IsDisplayHdrActive(d.DeviceId) != fallbackHdr);
        }

        private static bool IsHevcEncoder(string encoderId) =>
            HdrHevcEncoders.Contains(encoderId, StringComparer.OrdinalIgnoreCase);

        private static bool IsHdrCapableEncoder(string encoderId) =>
            HdrHevcEncoders.Contains(encoderId, StringComparer.OrdinalIgnoreCase) ||
            HdrAv1Encoders.Contains(encoderId, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns an available HDR-capable (10-bit HEVC/AV1) encoder for an HDR recording, or null
        /// if none is available. Keeps the user's encoder if it already supports HDR; otherwise
        /// substitutes the same vendor's HEVC (preferred) or AV1 encoder, falling back to any
        /// available HEVC/AV1 encoder (e.g. for software-encoder users who still have a GPU path).
        /// </summary>
        private static string? ResolveHdrEncoder(string userEncoderId, List<Codec> availableCodecs)
        {
            bool Available(string id) =>
                availableCodecs.Any(c => string.Equals(c.InternalEncoderId, id, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(userEncoderId) && IsHdrCapableEncoder(userEncoderId) && Available(userEncoderId))
                return userEncoderId;

            if (!string.IsNullOrEmpty(userEncoderId) && HdrEncoderSubstitutes.TryGetValue(userEncoderId, out var substitutes))
            {
                foreach (var candidate in substitutes)
                    if (Available(candidate)) return candidate;
            }

            foreach (var candidate in HdrHevcEncoders)
                if (Available(candidate)) return candidate;
            foreach (var candidate in HdrAv1Encoders)
                if (Available(candidate)) return candidate;

            return null;
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

                GeneralUtils.SetProcessPriority(ProcessPriorityClass.Normal);

                RecordingPreviewService.OnRecordingStopped();

                StopGameCaptureHookTimeoutTimer();
                StopDiskSpaceMonitor();

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

                    NotifyIconService.SetNotifyIconStatus(NotifyIconState.Idle);

                    Log.Information("Replay buffer stopped and disposed.");

                    _ = GameIntegrationService.Shutdown();

                    // Reload content list
                    await SettingsService.LoadContentFromFolderIntoState(false);
                }
                else if (!isReplayBufferMode && !isHybridMode && _output != null)
                {
                    // Stop standard recording
                    if (AppState.Instance.Recording != null)
                        AppState.Instance.UpdateRecordingEndTime(DateTime.Now);

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

                    NotifyIconService.SetNotifyIconStatus(NotifyIconState.Idle);

                    Log.Information("Recording stopped and disposed.");

                    _ = GameIntegrationService.Shutdown();

                    // Might be null or empty if the recording failed to start
                    if (AppState.Instance.Recording != null && AppState.Instance.Recording.FilePath != null)
                    {
                        // Check if we should discard the session due to no manual bookmarks
                        bool hasManualBookmarks = AppState.Instance.Recording.Bookmarks.Any(b => b.Type == BookmarkType.Manual);
                        if (Settings.Instance.DiscardSessionsWithoutBookmarks && !hasManualBookmarks)
                        {
                            Log.Information("Discarding session recording without manual bookmarks");
                            try
                            {
                                if (File.Exists(AppState.Instance.Recording.FilePath))
                                {
                                    File.Delete(AppState.Instance.Recording.FilePath);
                                    Log.Information($"Deleted video file: {AppState.Instance.Recording.FilePath}");
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
                            await EnsureFileReady(AppState.Instance.Recording.FilePath!);

                            int? igdbId = !string.IsNullOrEmpty(AppState.Instance.Recording.ExePath)
                                ? GameUtils.GetIgdbIdFromExePath(AppState.Instance.Recording.ExePath)
                                : null;
                            await ContentService.CreateMetadataFile(AppState.Instance.Recording.FilePath!, Content.ContentType.Session, AppState.Instance.Recording.Game, AppState.Instance.Recording.Bookmarks, igdbId: igdbId, audioTrackNames: AppState.Instance.Recording.AudioTrackNames);
                            await ContentService.CreateThumbnail(AppState.Instance.Recording.FilePath!, Content.ContentType.Session);
                            await ContentService.CreateWaveformFile(AppState.Instance.Recording.FilePath!, Content.ContentType.Session);

                            Log.Information($"Recording details:");
                            Log.Information($"Start Time: {AppState.Instance.Recording.StartTime}");
                            Log.Information($"End Time: {AppState.Instance.Recording.EndTime}");
                            Log.Information($"Duration: {AppState.Instance.Recording.Duration}");
                            Log.Information($"File Path: {AppState.Instance.Recording.FilePath}");
                        }
                    }

                    await SettingsService.LoadContentFromFolderIntoState(false);
                }
                else if (isHybridMode)
                {
                    if (AppState.Instance.Recording != null)
                        AppState.Instance.UpdateRecordingEndTime(DateTime.Now);

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

                    NotifyIconService.SetNotifyIconStatus(NotifyIconState.Idle);

                    Log.Information("Hybrid: All outputs stopped and disposed.");

                    _ = GameIntegrationService.Shutdown();

                    if (AppState.Instance.Recording != null && AppState.Instance.Recording.FilePath != null)
                    {
                        // Check if we should discard the session due to no manual bookmarks
                        bool hasManualBookmarks = AppState.Instance.Recording.Bookmarks.Any(b => b.Type == BookmarkType.Manual);
                        if (Settings.Instance.DiscardSessionsWithoutBookmarks && !hasManualBookmarks)
                        {
                            Log.Information("Hybrid: Discarding session recording without manual bookmarks");
                            try
                            {
                                if (File.Exists(AppState.Instance.Recording.FilePath))
                                {
                                    File.Delete(AppState.Instance.Recording.FilePath);
                                    Log.Information($"Deleted video file: {AppState.Instance.Recording.FilePath}");
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
                            await EnsureFileReady(AppState.Instance.Recording.FilePath!);

                            int? igdbId = !string.IsNullOrEmpty(AppState.Instance.Recording.ExePath)
                                ? GameUtils.GetIgdbIdFromExePath(AppState.Instance.Recording.ExePath)
                                : null;
                            await ContentService.CreateMetadataFile(AppState.Instance.Recording.FilePath!, Content.ContentType.Session, AppState.Instance.Recording.Game, AppState.Instance.Recording.Bookmarks, igdbId: igdbId, audioTrackNames: AppState.Instance.Recording.AudioTrackNames);
                            await ContentService.CreateThumbnail(AppState.Instance.Recording.FilePath!, Content.ContentType.Session);
                            await ContentService.CreateWaveformFile(AppState.Instance.Recording.FilePath!, Content.ContentType.Session);
                        }
                    }

                    await SettingsService.LoadContentFromFolderIntoState(false);
                }
                else
                {
                    DisposeOutput();
                    DisposeSources();
                    DisposeEncoders();
                    AppState.Instance.Recording = null;
                    AppState.Instance.PreRecording = null;
                }

                await StorageService.EnsureStorageBelowLimit();

                // Reset hooked executable file name and captured dimensions
                _hookedExecutableFileName = null;
                CapturedWindowWidth = null;
                CapturedWindowHeight = null;
                _isHdrRecording = false;
                _hdrEncoderId = null;

                // If the recording ends before it started, don't do anything
                if (AppState.Instance.Recording == null || (!isReplayBufferMode && AppState.Instance.Recording.FilePath == null))
                {
                    AppState.Instance.PreRecording = null;
                    return;
                }

                // Get the file path before nullifying the recording (FilePath is not null at this point because of the previous check)
                string filePath = AppState.Instance.Recording.FilePath!;

                // Get the bookmarks before nullifying the recording
                List<Bookmark> bookmarks = AppState.Instance.Recording.Bookmarks;

                // Reset the recording and pre-recording
                AppState.Instance.Recording = null;
                AppState.Instance.PreRecording = null;

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

                // Switch output audio: mute desktop sources and unmute game/discord sources
                var audioOutputMode = Settings.Instance.AudioOutputMode;
                if (audioOutputMode != AudioOutputMode.All)
                {
                    foreach (var desktopSource in _desktopSources)
                    {
                        try { desktopSource.IsMuted = true; }
                        catch (Exception ex) { Log.Warning($"Failed to mute desktop source: {ex.Message}"); }
                    }
                    Log.Information("Muted desktop audio sources (game hooked, using capture_audio)");

                    if (audioOutputMode == AudioOutputMode.GameAndDiscord && _discordAudioSource != null)
                    {
                        try { _discordAudioSource.IsMuted = false; }
                        catch (Exception ex) { Log.Warning($"Failed to unmute Discord source: {ex.Message}"); }
                        Log.Information("Unmuted Discord audio source (game hooked)");
                    }
                }

                if (AppState.Instance.Recording != null)
                {
                    AppState.Instance.Recording.IsUsingGameHook = true;
                    _ = MessageService.SendStateToFrontend("Updated game hook");
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

            // Switch output audio back: unmute desktop sources and mute discord source
            var audioOutputMode = Settings.Instance.AudioOutputMode;
            if (audioOutputMode != AudioOutputMode.All)
            {
                foreach (var desktopSource in _desktopSources)
                {
                    try { desktopSource.IsMuted = false; }
                    catch (Exception ex) { Log.Warning($"Failed to unmute desktop source: {ex.Message}"); }
                }
                Log.Information("Unmuted desktop audio sources (game unhooked, falling back to desktop audio)");

                if (audioOutputMode == AudioOutputMode.GameAndDiscord && _discordAudioSource != null)
                {
                    try { _discordAudioSource.IsMuted = true; }
                    catch (Exception ex) { Log.Warning($"Failed to mute Discord source: {ex.Message}"); }
                    Log.Information("Muted Discord audio source (game unhooked)");
                }
            }
        }

        private static void OnReplaySaved(nint calldata)
        {
            _replaySaved = true;
            Log.Information("Replay buffer saved callback received");
        }

        /// <summary>
        /// Fires whenever an output stops. OBS reports OBS_OUTPUT_SUCCESS (0) for normal stops
        /// (including ones Segra initiates). Any negative code means OBS stopped the output on its
        /// own (disk full, encoder error, etc.), so we tear down our state and notify the user.
        /// Runs on an OBS thread, so heavy work is dispatched off it.
        /// </summary>
        private static void OnOutputStopped(nint calldata)
        {
            int code = (int)Calldata.GetInt(calldata, "code");

            if (code == OBS_OUTPUT_SUCCESS)
                return;

            // Segra already initiated the stop; the teardown is running, so don't double-handle.
            if (_isStoppingOrStopped)
            {
                Log.Warning($"Output stopped with code {code} ({GetOutputCodeName(code)}) while already stopping.");
                return;
            }

            // In hybrid mode both outputs can stop together (same drive), so only handle the first.
            if (Interlocked.CompareExchange(ref _unexpectedStopHandled, 1, 0) != 0)
            {
                Log.Warning($"Output stopped with code {code} ({GetOutputCodeName(code)}); an unexpected stop is already being handled.");
                return;
            }

            // Capture the output's error text while it is still alive (we're on the OBS thread).
            // OBS only reports a coarse code (e.g. the MP4/ffmpeg muxer reports a full disk as
            // OBS_OUTPUT_ENCODE_ERROR), so the actual cause - "No space left on device", a path/
            // permission problem, an encoder error, etc. - lives only in this string. We surface it
            // directly rather than guessing from the code.
            string? lastError = _output?.LastError;
            if (string.IsNullOrEmpty(lastError))
                lastError = _bufferOutput?.LastError;

            Log.Error($"OBS stopped the recording output unexpectedly (code {code}: {GetOutputCodeName(code)}); last error: {lastError ?? "(none)"}");
            _ = Task.Run(() => HandleUnexpectedOutputStop(code, lastError));
        }

        /// <summary>
        /// Notifies the user about an unexpected output stop with a Segra-friendly message,
        /// then brings Segra's recording state in line with OBS (which already tore the output down).
        /// </summary>
        private static async Task HandleUnexpectedOutputStop(int code, string? lastError)
        {
            try
            {
                // The game is still in the foreground, so stop the detection loop from immediately
                // retrying into the same failure until the user switches foreground window.
                GameDetectionService.PreventRetryRecording = true;

                var (title, description) = MapOutputStopToMessage(code, lastError);
                await ShowModal(title, description, "error");
                _ = Task.Run(() => PlaySound("error"));
            }
            catch (Exception ex)
            {
                Log.Error($"Error notifying frontend of unexpected output stop: {ex.Message}");
            }
            finally
            {
                // OBS has already stopped the output; run the normal teardown to clean up sources,
                // encoders, state and reload the content list.
                await StopRecording();
            }
        }

        /// <summary>
        /// Maps the failure OBS reports - the coarse stop code plus the raw last-error string OBS
        /// writes (see obs-ffmpeg-mux.c / ffmpeg-mux.c / obs-nvenc) - to a clean Segra message.
        /// OBS's own text is never shown to the user; it is only inspected here to pick the message.
        /// The string is matched first because the code is unreliable (e.g. the MP4 muxer reports a
        /// full disk as OBS_OUTPUT_ENCODE_ERROR with "No space left on device" only in the string).
        /// </summary>
        private static (string Title, string Description) MapOutputStopToMessage(int code, string? lastError)
        {
            string e = lastError ?? string.Empty;
            bool Has(string sub) => e.Contains(sub, StringComparison.OrdinalIgnoreCase);

            // Out of disk space: muxer subprocess stderr "Error writing to '<path>', No space left on device"
            if (code == OBS_OUTPUT_NO_SPACE || Has("No space left on device") || Has("ENOSPC"))
            {
                return ("Recording stopped: out of disk space",
                    "The drive ran out of space while recording, so the recording was stopped and may be incomplete. Free up some space and try again.");
            }

            // Recording helper process could not start (HelperProcessFailed) - usually antivirus blocking
            if (Has("recording helper process"))
            {
                return ("Recording stopped: helper process blocked",
                    "The recording helper process could not run. It may have been blocked or removed by antivirus or security software. Add Segra to your antivirus exclusions and try again.");
            }

            // Cannot write to the recording folder: "Unable to write to %1", "Couldn't open '<path>', Permission denied"
            if (code == OBS_OUTPUT_BAD_PATH || Has("Unable to write to") || Has("Couldn't open") ||
                Has("Permission denied") || Has("Access is denied"))
            {
                return ("Recording stopped: cannot write to folder",
                    "Segra could not write the recording to your selected folder. Make sure the folder still exists and that your account is allowed to write to it.");
            }

            // Encoder failure: NVENC / CUDA / codec errors (obs-nvenc sets these on the encoder)
            if (Has("NVENC") || Has("CUDA") || Has("codec"))
            {
                return ("Recording stopped: encoder error",
                    "The video encoder failed while recording, so the recording was stopped. Update your graphics drivers or try a different encoder in settings, then start again.");
            }

            // HDR enabled but the encoder cannot encode it (OBS reports codec-specific strings).
            if (code == OBS_OUTPUT_HDR_DISABLED || Has("Rec. 2100") || Has("10bitUnsupported") ||
                Has("8bitUnsupportedHdr") || Has("HdrUnsupported"))
            {
                return ("Recording stopped: HDR not supported by encoder",
                    "The recording stopped because the selected encoder cannot record HDR. Update your graphics drivers, or switch to an encoder that supports HDR (such as HEVC or AV1), then start again.");
            }

            // Output settings not supported by the selected encoder/format
            if (code == OBS_OUTPUT_UNSUPPORTED)
            {
                return ("Recording stopped: unsupported settings",
                    "The recording stopped because the current output settings are not supported. Try a different encoder or format in settings, then start again.");
            }

            // Any other write failure reported by the muxer ("Error writing to '<path>', <reason>")
            if (Has("Error writing to"))
            {
                return ("Recording stopped: write error",
                    "An error occurred while writing the recording, so it was stopped and may be incomplete. Check the log for more details.");
            }

            // Remaining encoder failures surface as OBS_OUTPUT_ENCODE_ERROR without a recognizable string
            if (code == OBS_OUTPUT_ENCODE_ERROR)
            {
                return ("Recording stopped: encoder error",
                    "The video encoder failed while recording, so the recording was stopped. Update your graphics drivers or try a different encoder in settings, then start again.");
            }

            return ("Recording stopped unexpectedly",
                "Recording was stopped unexpectedly and the file may be incomplete. Check the log for more details.");
        }

        private static string GetOutputCodeName(int code) => code switch
        {
            OBS_OUTPUT_SUCCESS => "OBS_OUTPUT_SUCCESS",
            OBS_OUTPUT_BAD_PATH => "OBS_OUTPUT_BAD_PATH",
            OBS_OUTPUT_CONNECT_FAILED => "OBS_OUTPUT_CONNECT_FAILED",
            OBS_OUTPUT_INVALID_STREAM => "OBS_OUTPUT_INVALID_STREAM",
            OBS_OUTPUT_ERROR => "OBS_OUTPUT_ERROR",
            OBS_OUTPUT_DISCONNECTED => "OBS_OUTPUT_DISCONNECTED",
            OBS_OUTPUT_UNSUPPORTED => "OBS_OUTPUT_UNSUPPORTED",
            OBS_OUTPUT_NO_SPACE => "OBS_OUTPUT_NO_SPACE",
            OBS_OUTPUT_ENCODE_ERROR => "OBS_OUTPUT_ENCODE_ERROR",
            OBS_OUTPUT_HDR_DISABLED => "OBS_OUTPUT_HDR_DISABLED",
            _ => $"UNKNOWN ({code})"
        };

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

            // Dispose Discord audio source
            if (_discordAudioSource != null)
            {
                try
                {
                    _discordAudioSource.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to dispose Discord audio source: {ex.Message}");
                }
                _discordAudioSource = null;
            }
        }

        public static void DisposeGameCaptureSource()
        {
            if (_gameCaptureItem != null)
            {
                try
                {
                    _gameCaptureItem.Remove();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to remove game capture scene item: {ex.Message}");
                }
                _gameCaptureItem = null;
            }

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

        private static void StartDiskSpaceMonitor()
        {
            StopDiskSpaceMonitor();

            _diskSpaceMonitorTimer = new System.Threading.Timer(
                OnDiskSpaceCheck,
                null,
                DiskSpaceCheckIntervalMs,
                DiskSpaceCheckIntervalMs
            );

            Log.Information($"Started disk space monitor (every {DiskSpaceCheckIntervalMs / 1000}s, stop below {GetRecordingFreeSpaceThresholdBytes() / (1024 * 1024)} MB free)");
        }

        // Estimates worst-case bytes/sec written to disk based on the configured rate control,
        // so the low-space threshold can scale with bitrate (a single 60s gap at a high bitrate
        // can otherwise burn through hundreds of MB before the next check).
        private static long EstimateRecordingBytesPerSecond()
        {
            int videoMbps = Settings.Instance.RateControl switch
            {
                "CBR" => Settings.Instance.Bitrate,
                "VBR" => Settings.Instance.MaxBitrate,
                // CRF/CQP are quality-based with no explicit cap; assume a high worst case.
                _ => Math.Max(Settings.Instance.MaxBitrate, QualityModeAssumedMbps)
            };

            // Add 1 Mbps of headroom for audio tracks (a few AAC tracks at 128 kbps each).
            long bitsPerSecond = (videoMbps + 1L) * 1_000_000L;
            return bitsPerSecond / 8L;
        }

        // Free-space threshold at which we stop recording: enough to cover one check interval of
        // writing (with margin) plus a finalization buffer, never below the absolute floor.
        private static long GetRecordingFreeSpaceThresholdBytes()
        {
            long intervalSeconds = DiskSpaceCheckIntervalMs / 1000;
            long perIntervalWithMargin = (long)(EstimateRecordingBytesPerSecond() * intervalSeconds * 1.5);
            const long finalizationBufferBytes = 128L * 1024 * 1024; // 128 MB to finalize the file
            long threshold = perIntervalWithMargin + finalizationBufferBytes;
            return Math.Max(StorageService.MinimumRecordingFreeSpaceBytes, threshold);
        }

        private static void StopDiskSpaceMonitor()
        {
            if (_diskSpaceMonitorTimer != null)
            {
                _diskSpaceMonitorTimer.Dispose();
                _diskSpaceMonitorTimer = null;
                Log.Information("Stopped disk space monitor");
            }
        }

        // Stops the recording when the drive runs low on space, while OBS can still finalize the file
        // cleanly. Runs on a thread pool thread (System.Threading.Timer).
        private static void OnDiskSpaceCheck(object? state)
        {
            try
            {
                long? freeBytes = StorageService.GetContentDriveFreeBytes();
                if (freeBytes == null || freeBytes.Value >= GetRecordingFreeSpaceThresholdBytes())
                    return;

                // Only act once, and not if a failure stop (e.g. OBS output stop) is already being handled.
                if (Interlocked.CompareExchange(ref _unexpectedStopHandled, 1, 0) != 0)
                    return;

                // Stop the timer so we don't fire again while tearing down.
                StopDiskSpaceMonitor();

                // The game is still in the foreground and the drive is still low, so stop the detection
                // loop from immediately retrying until the user switches foreground window.
                GameDetectionService.PreventRetryRecording = true;

                double freeMb = freeBytes.Value / (1024.0 * 1024.0);
                long thresholdMb = GetRecordingFreeSpaceThresholdBytes() / (1024 * 1024);
                Log.Warning($"Recording drive low on space ({freeMb:F0} MB free, threshold {thresholdMb} MB). Stopping recording to finalize the file safely.");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ShowModal("Recording stopped: running low on disk space",
                            $"The recording drive is running low on space ({freeMb:F0} MB free), so recording was stopped to save the file safely. Free up some space before recording again.",
                            "error");
                        _ = Task.Run(() => PlaySound("error"));
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error notifying frontend of low disk space stop: {ex.Message}");
                    }
                    finally
                    {
                        // Segra-initiated graceful stop: OBS finalizes the file, then the output's
                        // stop signal fires with OBS_OUTPUT_SUCCESS and is ignored by OnOutputStopped.
                        await StopRecording();
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Warning($"Disk space monitor check failed: {ex.Message}");
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
            if (_displayItem != null)
            {
                try
                {
                    Log.Information("Removing display scene item from scene");
                    _displayItem.Remove();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to remove display scene item: {ex.Message}");
                }
                _displayItem = null;
            }

            if (_displaySource != null)
            {
                try
                {
                    Log.Information("Disposing display source (expect OBS 'source destroyed' log to confirm WGC cleanup)");
                    _displaySource.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to dispose display source: {ex.Message}");
                }
                _displaySource = null;
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
            _outputStoppedConnection?.Dispose();
            _outputStoppedConnection = null;
            _bufferStoppedConnection?.Dispose();
            _bufferStoppedConnection = null;
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
                    // Fail fast instead of the default 100s timeout when unreachable.
                    client.Timeout = TimeSpan.FromSeconds(15);
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

            if (isUpdate)
            {
                // We need to reinstall the Segra app to apply the update, because all OBS resources are placed in the app directory
                Settings.Instance.PendingOBSUpdate = true;
                SettingsService.SaveSettings();
                await UpdateService.ForceReinstallCurrentVersionAsync();
                await ShowModal("OBS Update", "Please restart Segra to apply the update.");
                return;
            }

            if (IsOBSInstalled() && !Settings.Instance.PendingOBSUpdate)
            {
                Log.Information("OBS is installed");
                // Refresh versions for the UI in the background; don't stall init on this network call.
                _ = AvailableOBSVersionsAsync();
                return;
            }

            await AvailableOBSVersionsAsync();

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
                versionToDownload = AppState.Instance.AvailableOBSVersions
                    .FirstOrDefault(v => v.Version == selectedVersion);

                if (versionToDownload == null)
                {
                    Log.Warning($"Selected OBS version {selectedVersion} not found in available versions. Using latest stable version.");
                }
            }

            // If no specific version was selected or found, use the latest non-beta version
            if (versionToDownload == null)
            {
                versionToDownload = AppState.Instance.AvailableOBSVersions
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
                    httpClient.Timeout = Timeout.InfiniteTimeSpan;

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

            // Throw so InitializeAsync shows the recorder-error modal instead of failing silently.
            Log.Error("No OBS versions available to install the recorder (version server unreachable).");
            throw new InvalidOperationException("No OBS versions available to install the recorder.");
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

            using var waveOut = new WasapiOut(AudioClientShareMode.Shared, 100);
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
                    AppState.Instance.Codecs.Add(new Codec { InternalEncoderId = encoderId, FriendlyName = friendlyName, IsHardwareEncoder = isHardware });
                }
                idx++;
            }

            Log.Information($"Total encoders found: {idx}");

            if (Settings.Instance.Codec == null)
            {
                Settings.Instance.Codec = SelectDefaultCodec(Settings.Instance.Encoder, AppState.Instance.Codecs);
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
