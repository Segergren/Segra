using Serilog;
using ObsKit.NET;
using ObsKit.NET.Scenes;
using Segra.Backend.App;
using ObsKit.NET.Outputs;
using ObsKit.NET.Sources;
using Segra.Backend.Core;
using System.Diagnostics;
using ObsKit.NET.Encoders;
using Segra.Backend.Games;
using Segra.Backend.Media;
using Segra.Backend.Shared;
using Segra.Backend.Platform;
using System.Net.Http.Json;
using System.IO.Compression;
using ObsKit.NET.Native.Types;
using Segra.Backend.Core.Models;
using System.Threading.Channels;
using Segra.Backend.Windows.Input;
using Segra.Backend.Windows.Storage;
using System.Text.RegularExpressions;
using static Segra.Backend.App.MessageService;
using static Segra.Backend.Shared.GeneralUtils;
#if WINDOWS
using Segra.Backend.Windows.Display;
#endif

namespace Segra.Backend.Recorder
{
    public static partial class OBSService
    {
        private const uint OBS_SOURCE_FLAG_FORCE_MONO = 1u << 1; // from obs.h

        [GeneratedRegex(@"BufferDesc\.Width:\s*(\d+)")]
        private static partial Regex BufferDescWidthRegex();

        [GeneratedRegex(@"BufferDesc\.Height:\s*(\d+)")]
        private static partial Regex BufferDescHeightRegex();

        public static bool IsInitialized { get; private set; }
        public static uint? CapturedWindowWidth { get; private set; } = null;
        public static uint? CapturedWindowHeight { get; private set; } = null;
        public static string? InstalledOBSVersion { get; private set; } = null;

        private static ObsContext? _obsContext;

        private static Scene? _mainScene;
        private static SceneItem? _gameCaptureItem;
        private static SceneItem? _displayItem;

        private static RecordingOutput? _output;
        private static ReplayBuffer? _bufferOutput;

        public static GameCapture? GameCaptureSource { get; set; }
        private static Source? _displaySource;
        private static readonly List<AudioInputCapture> _micSources = [];
        private static readonly List<AudioOutputCapture> _desktopSources = [];
        private static readonly List<(string Name, string Window, Source Source)> _voiceChatSources = [];

        // Mixer mask of the shared "Voice Chat" track, so sources created mid-recording land on the same track
        private static uint _voiceChatMixerMask = 1u << 0;

        private static readonly (string Name, string Window)[] VoiceChatApps =
        [
            ("Discord", "Discord:Chrome_WidgetWin_1:Discord.exe"),
            ("TeamSpeak", "TeamSpeak:Chrome_WidgetWin_1:TeamSpeak.exe"),
            ("TeamSpeak 3", "TeamSpeak 3:Qt5152QWindowIcon:ts3client_win64.exe"),
            ("TeamSpeak 3", "TeamSpeak 3:Qt5152QWindowIcon:ts3client_win32.exe"),
        ];

        private static VideoEncoder? _videoEncoder;
        private static readonly List<AudioEncoder> _audioEncoders = [];

        private static string? _hookedExecutableFileName;
        private static System.Threading.Timer? _gameCaptureHookTimeoutTimer = null;
        private static bool _isStillHookedAfterUnhook = false;

        // Periodic low-disk-space monitor while recording
        private static System.Threading.Timer? _diskSpaceMonitorTimer = null;
        private const int DiskSpaceCheckIntervalMs = 60000; // 1 minute
        // Quality-based rate controls (CRF/CQP) have no bitrate cap, so assume a high worst case when sizing headroom
        private const int QualityModeAssumedMbps = 150;

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

        // Correlates the one in-flight replay save with OBS's 'saved' signal. The signal
        // carries no path or identity and OBS has no failure signal at all, so only one save
        // may be in flight at a time (_replaySaveSemaphore) and completion is delivered
        // through the request's task: the saved file path on success, null on failure.
        private static ReplaySaveRequest? _activeReplaySave;
        private static readonly object _replaySaveLock = new();
        private static readonly SemaphoreSlim _replaySaveSemaphore = new(1, 1);

        // True when a save timed out with its OBS-side mux state unknown. Until the old mux
        // resolves (late 'saved' signal, failure line, or output teardown), arming a new
        // request is unsafe: the old mux's signal would resolve it with the wrong file.
        // Guarded by _replaySaveLock.
        private static bool _previousSaveIndeterminate;

        private sealed class ReplaySaveRequest
        {
            // Recording context captured when the save was requested; a save to slow storage
            // (e.g. a network share) can outlive the game session that produced it.
            public required string Game { get; init; }
            public int? IgdbId { get; init; }
            public List<string>? AudioTrackNames { get; init; }
            public string? FailureReason;
            public readonly TaskCompletionSource<string?> Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Ensures an unexpected stop is handled once even if multiple outputs stop together (e.g. hybrid mode)
        private static int _unexpectedStopHandled = 0;

        /// <summary>
        /// Gets whether the game capture is currently hooked.
        /// Uses the built-in IsHooked property from OBSKit.NET.
        /// </summary>
        private static bool IsGameCaptureHooked => GameCaptureSource?.IsHooked ?? false;

        private static readonly SemaphoreSlim _stopRecordingSemaphore = new(1, 1);

        // Log processing queue - prevents OBS thread from blocking on log operations
        private static readonly Channel<(int level, string message)> _logChannel =
            Channel.CreateUnbounded<(int, string)>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        public static async Task<bool> SaveReplayBuffer()
        {
            // One save at a time: OBS defers a save requested while a previous one is still
            // muxing, and its 'saved' signal carries no identity, so correlation is only sound
            // with a single save in flight. Concurrent requests queue here and run in order.
            await _replaySaveSemaphore.WaitAsync();
            try
            {
                if (_bufferOutput == null || !_bufferOutput.IsActive)
                {
                    Log.Warning("Cannot save replay buffer: buffer is not active");
                    return false;
                }

                string? exePath = AppState.Instance.Recording?.ExePath;
                var request = new ReplaySaveRequest
                {
                    Game = AppState.Instance.Recording?.Game ?? "Unknown",
                    IgdbId = !string.IsNullOrEmpty(exePath) ? GameUtils.GetIgdbIdFromExePath(exePath) : null,
                    AudioTrackNames = AppState.Instance.Recording?.AudioTrackNames
                };

                lock (_replaySaveLock)
                    _activeReplaySave = request;

                // A previously timed-out save left OBS's mux state unknown; arming a request
                // now would let that old mux's 'saved' signal resolve it with the wrong file.
                if (!await WaitForPriorSaveResolutionAsync(GetReplaySaveExpectedTimeout()))
                {
                    Log.Warning("Cannot save replay buffer: a previous save is still unresolved.");
                    await MessageService.ShowModal("Replay Save Failed", "A previous replay save is still being written. Try again once it finishes.", "error");
                    return false;
                }

                Log.Information("Attempting to save replay buffer...");
                try
                {
                    _bufferOutput.Save();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to save replay buffer: {ex.Message}");
                    return false;
                }

                string? savedPath = await WaitForReplaySavedAsync(request);

                if (string.IsNullOrEmpty(savedPath))
                {
                    string reason = request.FailureReason ?? "OBS did not confirm the replay was written. See the logs for details.";
                    Log.Error($"Replay buffer save failed: {reason}");
                    await MessageService.ShowModal("Replay Save Failed", reason, "error");
                    return false;
                }

                savedPath = PathUtils.Normalize(savedPath);
                Log.Information($"Replay buffer saved to: {savedPath}");

                // The file is fully written at this point; let the frontend confirm the save.
                _ = MessageService.SendFrontendMessage("ReplayBufferSaved", new { });

                // Ensure file is fully written to disk/network before thumbnail generation
                await EnsureFileReady(savedPath);

                // Create metadata for the buffer recording
                await ContentService.CreateMetadataFile(savedPath, Content.ContentType.Buffer, request.Game, igdbId: request.IgdbId, audioTrackNames: request.AudioTrackNames);
                await ContentService.CreateThumbnail(savedPath, Content.ContentType.Buffer);
                await ContentService.CreateWaveformFile(savedPath, Content.ContentType.Buffer);

                // Reload content list to include the new buffer file
                await SettingsService.LoadContentFromFolderIntoState(true);

                Log.Information("Replay buffer save process completed successfully");

                // Restart replay buffer so subsequent saves only include new footage, unless a
                // recording stop began while the save was completing.
                if (!_isStoppingOrStopped)
                    await ResetReplayBuffer();

                return true;
            }
            finally
            {
                lock (_replaySaveLock)
                    _activeReplaySave = null;
                _replaySaveSemaphore.Release();
            }
        }

        /// <summary>
        /// Waits for OBS to finish writing the replay. The 'saved' signal fires only after the
        /// mux process has written the entire file, which is paced by the destination - on a
        /// network share this can take minutes. OBS has no failure signal, so this wait is
        /// resolved by OnReplaySaved (success), a mux failure line in the OBS log
        /// (ProcessLogQueueAsync), buffer teardown (DisposeOutput), or the backstop below.
        /// </summary>
        private static async Task<string?> WaitForReplaySavedAsync(ReplaySaveRequest request)
        {
            TimeSpan expected = GetReplaySaveExpectedTimeout();
            TimeSpan backstop = TimeSpan.FromMinutes(15);

            Task<string?> signal = request.Signal.Task;

            try
            {
                return await signal.WaitAsync(expected);
            }
            catch (TimeoutException) { }

            Log.Warning($"Replay save not confirmed after {expected.TotalSeconds:F0}s; the destination may be slow (network share?). Waiting up to {backstop.TotalMinutes:F0} minutes.");

            try
            {
                return await signal.WaitAsync(backstop - expected);
            }
            catch (TimeoutException) { }

            // OBS never told us how the save ended, so its mux may still be writing; mark the
            // state indeterminate so the next save waits for it instead of arming a request
            // the old mux's 'saved' signal would resolve with the wrong file.
            FailActiveReplaySave($"The replay was still being written after {backstop.TotalMinutes:F0} minutes.", obsSideStateUnknown: true);
            return await signal;
        }

        /// <summary>
        /// How long a legitimate save is expected to take: worst case flushes the whole buffer,
        /// assuming conservative ~5 MB/s sustained write for slow/network storage.
        /// </summary>
        private static TimeSpan GetReplaySaveExpectedTimeout()
        {
            int maxSizeMb = _activeEffectiveSettings?.ReplayBufferMaxSize ?? Settings.Instance.ReplayBufferMaxSize;
            return TimeSpan.FromSeconds(Math.Clamp(maxSizeMb / 5.0, 60d, 600d));
        }

        /// <summary>
        /// Fails the in-flight replay save, if any. Used when OBS logs a mux error (there is
        /// no failure signal) and when the buffer output is torn down mid-save. Pass
        /// obsSideStateUnknown when the OBS-side mux may still be running (backstop timeout).
        /// </summary>
        private static void FailActiveReplaySave(string reason, bool obsSideStateUnknown = false)
        {
            lock (_replaySaveLock)
            {
                if (_activeReplaySave == null || _activeReplaySave.Signal.Task.IsCompleted)
                    return;

                _activeReplaySave.FailureReason = reason;
                _activeReplaySave.Signal.TrySetResult(null);

                if (obsSideStateUnknown)
                    _previousSaveIndeterminate = true;
            }
        }

        /// <summary>
        /// Waits for a previously timed-out save's OBS-side mux to resolve (late 'saved'
        /// signal, failure line, or teardown). Returns false if it is still unresolved.
        /// </summary>
        private static async Task<bool> WaitForPriorSaveResolutionAsync(TimeSpan limit)
        {
            long deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
            while (true)
            {
                lock (_replaySaveLock)
                {
                    if (!_previousSaveIndeterminate)
                        return true;
                }

                if (Environment.TickCount64 >= deadline)
                    return false;

                await Task.Delay(500);
            }
        }

        /// <summary>
        /// Called when OBS logs a replay-mux failure line (scoped to 'replay_buffer_output' by
        /// the caller). Fails the in-flight save, or resolves a previously indeterminate one.
        /// </summary>
        private static void OnReplayMuxFailureLine(string logLine)
        {
            lock (_replaySaveLock)
            {
                if (_activeReplaySave != null && !_activeReplaySave.Signal.Task.IsCompleted)
                {
                    _activeReplaySave.FailureReason = $"OBS reported a write failure: {logLine}";
                    _activeReplaySave.Signal.TrySetResult(null);
                }
                else if (_previousSaveIndeterminate)
                {
                    // The mux errored out, so nothing is writing anymore; new saves may proceed.
                    Log.Warning("A previously timed-out replay save has now failed in OBS.");
                    _previousSaveIndeterminate = false;
                }
            }
        }

        /// <summary>
        /// Lets an in-flight replay save finish before the buffer is stopped. Disposing the
        /// output blocks on the mux thread anyway (libobs joins it in destroy), so this wait
        /// is nearly free and turns a would-be orphaned file into a proper clip. The limit
        /// matches what the save flow itself considers normal for this buffer size.
        /// </summary>
        private static async Task WaitForInFlightReplaySaveAsync()
        {
            Task<string?>? pending;
            lock (_replaySaveLock)
                pending = _activeReplaySave?.Signal.Task;

            if (pending == null || pending.IsCompleted)
                return;

            TimeSpan limit = GetReplaySaveExpectedTimeout();
            Log.Information($"Waiting up to {limit.TotalSeconds:F0}s for the in-flight replay save before stopping the replay buffer...");
            try
            {
                await pending.WaitAsync(limit);
            }
            catch (TimeoutException) { }
        }

        /// <summary>
        /// Stops and restarts the replay buffer so that subsequent saves
        /// only contain footage recorded after the last save.
        /// </summary>
        private static async Task ResetReplayBuffer()
        {
            // Take the stop semaphore so StopRecording cannot dispose the output between our
            // checks and Stop/Start. If a stop already holds it, skip the reset entirely.
            if (!await _stopRecordingSemaphore.WaitAsync(0))
            {
                Log.Information("Skipping replay buffer reset: a recording stop is in progress.");
                return;
            }

            try
            {
                var buffer = _bufferOutput;
                if (buffer == null || _isStoppingOrStopped)
                    return;

                Log.Information("Resetting replay buffer...");

                bool stopped = buffer.Stop(waitForCompletion: true, timeoutMs: 30000);

                if (!stopped)
                {
                    Log.Warning("Replay buffer did not stop within timeout for reset. Forcing stop.");
                    buffer.ForceStop();
                    await Task.Delay(500);
                }

                bool started = buffer.Start();

                if (!started)
                {
                    string error = buffer.LastError ?? "Unknown error";
                    Log.Error($"Failed to restart replay buffer after reset: {error}");
                }
                else
                {
                    Log.Information("Replay buffer restarted successfully");
                }
            }
            finally
            {
                _stopRecordingSemaphore.Release();
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

                    // The replay mux thread has no failure signal; these warn lines from
                    // obs-ffmpeg-mux.c are the only evidence a replay save failed. Fail the
                    // pending save immediately instead of waiting out the timeout. Scoped to
                    // the replay output's log prefix ("[ffmpeg muxer: 'replay_buffer_output']")
                    // so a session/HLS muxer failure can't kill a healthy replay save.
                    if ((_activeReplaySave != null || _previousSaveIndeterminate) &&
                        formattedMessage.Contains("'replay_buffer_output'") &&
                        (formattedMessage.Contains("Failed to create process pipe") ||
                         formattedMessage.Contains("Could not write headers for file") ||
                         formattedMessage.Contains("Could not write packet for file") ||
                         formattedMessage.Contains("Failed to create muxer thread") ||
                         formattedMessage.Contains("Could not save buffer because encoders paused")))
                    {
                        OnReplayMuxFailureLine(formattedMessage.Trim());
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
#if WINDOWS
                await MessageService.ShowModal(
                    "Recorder Error",
                    "The recorder installation failed. Please check your internet connection and try again. If you have any games running, please close them and restart Segra.",
                    "error",
                    "Could not install recorder"
                );
#else
                await MessageService.ShowModal(
                    "Recorder not found",
                    "Segra's Linux recorder needs OBS Studio's libraries (libobs). Install OBS with your package manager, for example:\n\n    sudo apt install obs-studio\n\nThen restart Segra.",
                    "error",
                    "OBS Studio not found"
                );
#endif
                AppState.Instance.HasLoadedObs = true;
                return;
            }

#if WINDOWS
            // Probe NVENC capabilities in the background (cached in AppData until the GPU,
            // driver or OBS bundle changes) so encoder setup can disable unsupported features
            // like b-frames. The test exe ships with the OBS bundle, so this must run after
            // CheckIfExistsOrDownloadAsync.
            NvencCapsService.StartProbe();
#endif

            if (Obs.IsInitialized)
                throw new Exception("Error: OBS is already initialized.");

            // Start the log queue processor before setting the log handler
            _ = Task.Run(ProcessLogQueueAsync);

            try
            {
                // Initialize OBS using ObsKit.NET fluent API
#if WINDOWS
                // Absolute paths so the game-capture inject-helper gets an absolute graphics-hook path.
                string baseDir = AppContext.BaseDirectory.Replace('\\', '/').TrimEnd('/');
                string obsModulePath = $"{baseDir}/obs-plugins/64bit/";
                string obsModuleDataPath = $"{baseDir}/data/obs-plugins/%module%/";
                string obsDataPath = $"{baseDir}/data/libobs/";
                Log.Information($"OBS runtime paths (absolute): data='{obsDataPath}', modules='{obsModulePath}'");
#else
                // The launcher/re-exec resolves the OBS runtime and passes paths via env vars.
                string obsModulePath = Environment.GetEnvironmentVariable("SEGRA_OBS_MODULE_PATH") ?? "./obs-plugins/";
                string obsModuleDataPath = Environment.GetEnvironmentVariable("SEGRA_OBS_MODULE_DATA_PATH") ?? "./data/obs-plugins/%module%/";
                string obsDataPath = Environment.GetEnvironmentVariable("SEGRA_OBS_DATA_PATH") ?? "./data/libobs/";
                Log.Information($"Linux OBS runtime: data='{obsDataPath}', modules='{obsModulePath}'");
#endif
                _obsContext = Obs.Initialize(config =>
                {
                    config
                        .WithLocale("en-US")
                        .WithDataPath(obsDataPath)
                        .WithModulePath(obsModulePath, obsModuleDataPath)
#if !WINDOWS
                        .ForHeadlessOperation()
#endif
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

                // Hotkeys register through OBS's own hotkey system, so this can only run
                // once OBS is initialized. A failure here must not be reported as an OBS
                // initialization failure - OBS itself is already up at this point.
                try
                {
                    KeybindCaptureService.Start();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to register keybind hotkeys");
                }

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

                KeybindCaptureService.Stop();

                // Manually clean up all resources since AutoDispose is false
                DisposeOutput();
                DisposeSources();
                DisposeEncoders();

                // Dispose the OBS context last
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
        private static void ResetVideoSettings(out bool is4by3, uint? customFps = null, uint? customOutputWidth = null, uint? customOutputHeight = null, string? customResolution = null)
        {
            SettingsService.GetPrimaryMonitorResolution(out uint baseWidth, out uint baseHeight);

            // Use custom values if provided, otherwise use defaults
            baseWidth = customOutputWidth ?? baseWidth;
            baseHeight = customOutputHeight ?? baseHeight;

            // Get the maximum height from resolution setting (per-game override may substitute the resolution)
            SettingsService.GetResolution(customResolution ?? Settings.Instance.Resolution, out uint maxWidth, out uint maxHeight);

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

        // Effective recording settings (global overlaid with per-game overrides) resolved at the start of
        // the active recording. Consumed by StartRecording, OnRecordingStopped and the keybind handler so
        // they all agree on the same values for the duration of the recording.
        private static EffectiveRecordingSettings? _activeEffectiveSettings;
        public static EffectiveRecordingSettings? ActiveEffectiveSettings => _activeEffectiveSettings;

        public static bool StartRecording(string name = "Manual Recording", string exePath = "Unknown", bool startManually = false, int? pid = null)
        {
            // Held for the whole call (not just a wait-then-release at entry) so Start and Stop can never interleave.
            _stopRecordingSemaphore.Wait();
            try
            {
                return StartRecordingCore(name, exePath, startManually, pid);
            }
            finally
            {
                _stopRecordingSemaphore.Release();
            }
        }

        private static bool StartRecordingCore(string name, string exePath, bool startManually, int? pid)
        {
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

            // Resolve global settings overlaid with any per-game overrides for this game.
            // Note: the static _activeEffectiveSettings is only published once the early-return guards
            // below have passed, so a blocked start attempt can never clobber an active recording's settings.
            EffectiveRecordingSettings eff = GameSettingsService.Resolve(exePath);

            bool isReplayBufferMode = eff.RecordingMode == RecordingMode.Buffer;
            bool isSessionMode = eff.RecordingMode == RecordingMode.Session;
            bool isHybridMode = eff.RecordingMode == RecordingMode.Hybrid;

            string fileName = Path.GetFileName(exePath);

            // Prevent starting if any output is already active
            if (_bufferOutput != null || _output != null)
            {
                Log.Information("A recording or replay buffer is already in progress.");
                AppState.Instance.PreRecording = null;
                return false;
            }

            // Publish the effective settings only now that we know no other recording is active, so a
            // blocked start can never overwrite the in-progress recording's settings. The disk-space
            // checks below intentionally run after this so they estimate using the per-game bitrate.
            _activeEffectiveSettings = eff;

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
#if WINDOWS
            try
            {
                if (!eff.EnableHdr)
                {
                    Log.Information("HDR recording is disabled in settings; recording in SDR.");
                }
                else
                {
                    // Base HDR on the monitor whose content we actually capture: for a game, the monitor
                    // the game window is on (so a game on an SDR monitor is never forced to PQ); for a
                    // manual recording, the selected display.
                    string? hdrTargetDeviceId = startManually
                        ? GetCaptureTargetDeviceId()
                        : ResolveGameHdrTargetDeviceId();

                    if (HdrDetectionService.IsDisplayHdrActive(hdrTargetDeviceId))
                    {
                        string userEncoderId = eff.Codec?.InternalEncoderId ?? string.Empty;
                        string? hdrEncoderId = EncoderInfo.FindHdrCapable(userEncoderId)?.Id;
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
            }
            catch (Exception ex)
            {
                Log.Warning($"HDR detection failed, recording in SDR: {ex.Message}");
                _isHdrRecording = false;
                _hdrEncoderId = null;
            }
#endif

            // Clean slate before creating new objects: dispose any stale scene/sources/encoders left by a
            // skipped or partial teardown. No-op on the normal path where StopRecording already cleaned up.
            DisposeOutput();
            DisposeSources();
            DisposeEncoders();

            // Configure video settings specifically for this recording/buffer
            ResetVideoSettings(out _, customFps: (uint)eff.FrameRate, customResolution: eff.Resolution);

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
#if WINDOWS
            else
            {
                // Add display capture first (bottom layer - fallback)
                AddMonitorCapture();

                // Create game capture source for automatic game detection
                try
                {
                    GameCaptureSource = new GameCapture("gameplay", GameCapture.CaptureMode.SpecificWindow);
                    GameCaptureSource.SetWindow($"*:*:{fileName}");
                    GameCaptureSource.Volume = eff.VolumeMultiplier;

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
                        customFps: (uint)eff.FrameRate,
                        customOutputWidth: windowWidth,
                        customOutputHeight: windowHeight,
                        customResolution: eff.Resolution
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
#else
            else
            {
                // Linux: graphics-hook game_capture does not exist; record the desktop via PipeWire.
                Log.Information("Linux game recording - using desktop (PipeWire) capture");
                AddMonitorCapture();
                _displayItem?.SetBounds(ObsBoundsType.ScaleInner, _currentBaseWidth, _currentBaseHeight).SetPosition(0, 0);
            }
#endif

            // Set scene as program output (channel 0)
            Obs.SetOutputSource(_mainScene);

            string encoderId = eff.Codec!.InternalEncoderId;
            if (_isHdrRecording && _hdrEncoderId != null)
                encoderId = _hdrEncoderId;
            Log.Information($"Using encoder: {encoderId}{(_isHdrRecording ? " (HDR)" : "")}");

            using var videoEncoderSettings = new ObsKit.NET.Core.Settings();
            videoEncoderSettings.Set("keyint_sec", 1);

            // Encoder families expose different settings schemas, so each is configured on its own
            // terms rather than configuring one and patching for the others. VAAPI (the Linux GPU
            // path) is the second family; NVENC/QSV/AMF/x264 share the schema below.
            if (IsVaapiEncoder(encoderId))
            {
                ConfigureVaapiVideoEncoder(videoEncoderSettings, encoderId, eff);
            }
            else
            {
                videoEncoderSettings.Set("preset", "Quality");
                // HEVC needs the Main 10 profile for 10-bit HDR; AV1 derives bit depth from the P010 input.
                videoEncoderSettings.Set("profile", _isHdrRecording && EncoderInfo.Get(encoderId)?.Codec == "hevc" ? "main10" : "high");
                videoEncoderSettings.Set("use_bufsize", true);
                videoEncoderSettings.Set("rate_control", eff.RateControl);

                switch (eff.RateControl)
                {
                    case "CBR":
                        int targetBitrateKbps = eff.Bitrate * 1000;
                        videoEncoderSettings.Set("bitrate", targetBitrateKbps);
                        videoEncoderSettings.Set("max_bitrate", targetBitrateKbps);
                        videoEncoderSettings.Set("bufsize", targetBitrateKbps);
                        break;

                    case "VBR":
                        int minBitrateKbps = eff.MinBitrate * 1000;
                        int maxBitrateKbps = eff.MaxBitrate * 1000;
                        videoEncoderSettings.Set("bitrate", minBitrateKbps);
                        videoEncoderSettings.Set("max_bitrate", maxBitrateKbps);
                        videoEncoderSettings.Set("bufsize", maxBitrateKbps);
                        break;

                    case "CRF":
                        // Software x264 path mainly; no explicit bitrate
                        videoEncoderSettings.Set("crf", eff.CrfValue);
                        break;

                    case "CQP":
                        // Hardware encoders (NVENC/QSV/AMF) often use cqp/cq; provide both cqp and qp for compatibility
                        videoEncoderSettings.Set("cqp", eff.CqLevel);
                        videoEncoderSettings.Set("qp", eff.CqLevel);
                        break;

                    default:
                        AppState.Instance.PreRecording = null;
                        throw new Exception("Unsupported Rate Control method.");
                }

                ApplyNvencBFrameLimit(videoEncoderSettings, encoderId);
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
                    .Fps((uint)eff.FrameRate)
                    .Sdr());

                encoderId = eff.Codec!.InternalEncoderId;
                videoEncoderSettings.Set("profile", "high");
                ApplyNvencBFrameLimit(videoEncoderSettings, encoderId);
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
                                var noiseSuppression = new Source("noise_suppress_filter", $"{sourceName}_NoiseSuppression");
                                noiseSuppression.Update(s =>
                                {
                                    s.Set("method", "rnnoise");
                                    s.Set("suppress_level", -30L);
                                });
                                micSource.AddFilter(noiseSuppression);
                                Log.Information($"Added RNNoise noise suppression filter to {sourceName}");
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

                        desktopSource.Volume = deviceSetting.Volume * eff.VolumeMultiplier;

                        _mainScene!.AddSource(desktopSource);
                        _desktopSources.Add(desktopSource);

                        Log.Information($"Added output device: {deviceSetting.Name} ({deviceSetting.Id}) as {sourceName} with volume {desktopSource.Volume}");
                    }
                }
            }

            // In GameAndDiscord mode, capture audio from running voice chat apps. Sources start muted
            // (desktop audio covers voice chat until the game hooks); apps launched mid-recording are
            // added via OnVoiceChatAppStarted.
            if (audioOutputMode == AudioOutputMode.GameAndDiscord && GameCaptureSource != null)
            {
                foreach (var app in VoiceChatApps)
                {
                    string processName = Path.GetFileNameWithoutExtension(app.Window.Split(':')[^1]);
                    if (IsProcessRunning(processName))
                        TryAddVoiceChatSource(app, muted: true);
                }
            }

            // Configure mixers and audio encoders based on setting.
            // If enabled: Track 1 = Full Mix, Tracks 2..6 = per-group isolated (up to 5 groups)
            // If disabled: Track 1 only (Full Mix)
            // Each group shares one isolated track; all voice chat apps form a single "Voice Chat" group.
            // In GameOnly/GameAndDiscord modes, desktop sources are fallback-only (full mix only).
            var trackGroups = new List<List<Source>>();
            foreach (var micSource in _micSources)
                trackGroups.Add([micSource]);
            foreach (var desktopSource in _desktopSources)
                trackGroups.Add([desktopSource]);

            int voiceChatGroupIndex = -1;
            if (audioOutputMode != AudioOutputMode.All && GameCaptureSource != null)
            {
                // Desktop sources are fallback-only: assign to full mix (Track 1) only, no separate tracks
                foreach (var desktopSource in _desktopSources)
                {
                    try { desktopSource.AudioMixers = 1u << 0; }
                    catch (Exception ex) { Log.Warning($"Failed to set mixer for fallback desktop source: {ex.Message}"); }
                }

                // Remove desktop sources from the list that gets separate tracks
                trackGroups = [];
                foreach (var micSource in _micSources)
                    trackGroups.Add([micSource]);
                trackGroups.Add([GameCaptureSource]);

                // The voice chat group is reserved even when currently empty so apps launched
                // mid-recording can still join its track (the encoders are fixed once recording starts)
                if (audioOutputMode == AudioOutputMode.GameAndDiscord)
                {
                    voiceChatGroupIndex = trackGroups.Count;
                    trackGroups.Add(_voiceChatSources.Select(v => v.Source).ToList());
                }
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
                if (audioOutputMode == AudioOutputMode.GameAndDiscord)
                    audioDeviceNames.Add("Voice Chat");
            }

            bool separateTracks = Settings.Instance.EnableSeparateAudioTracks;
            int maxTracks = 6; // OBS supports up to 6 audio tracks
            int perSourceTracks = separateTracks ? Math.Min(trackGroups.Count, maxTracks - 1) : 0; // tracks 2..6 for groups
            int trackCount = 1 + perSourceTracks; // Track 1 is always the full mix

            _voiceChatMixerMask = 1u << 0;
            for (int i = 0; i < trackGroups.Count; i++)
            {
                // Always include Track 1 (bit 0) as a full mix
                uint mixersMask = 1u << 0;

                // If enabled, give first 5 groups their own isolated tracks on 2..6 (bits 1..5)
                if (separateTracks && i < (maxTracks - 1))
                {
                    mixersMask |= (uint)(1 << (i + 1));
                }
                else if (separateTracks)
                {
                    Log.Warning($"Audio group index {i} exceeds {maxTracks - 1} dedicated tracks. It will be available in the master mix (Track 1) only.");
                }

                if (i == voiceChatGroupIndex)
                    _voiceChatMixerMask = mixersMask;

                foreach (var source in trackGroups[i])
                {
                    try
                    {
                        source.AudioMixers = mixersMask;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Failed to set mixers for audio source in group {i}: {ex.Message}");
                    }
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

                _bufferOutput = new ReplayBuffer("replay_buffer_output", eff.ReplayBufferDuration, eff.ReplayBufferMaxSize);
                _bufferOutput.SetDirectory(bufferDir);
                _bufferOutput.SetFilenameFormat("%CCYY-%MM-%DD_%hh-%mm-%ss");
                _bufferOutput.Update(s => s.Set("extension", "mp4").Set("tracks", (long)bufferTracksMask));

                _bufferOutput.WithVideoEncoder(_videoEncoder);
                for (int t = 0; t < _audioEncoders.Count; t++)
                {
                    _bufferOutput.WithAudioEncoder(_audioEncoders[t], track: t);
                }

                // Connect handler for replay saved
                _bufferOutput!.Saved += OnReplaySaved;

                // Detect unexpected stops (e.g. disk full mid-recording) so we can notify the user
                _bufferOutput!.Stopped += OnOutputStopped;
            }

            if (isSessionMode || isHybridMode)
            {
                videoOutputPath = $"{sessionDir}/{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";

                uint recordTracksMask = (1u << trackCount) - 1u;

                // Try Hybrid MP4 (crash-resilient, chapter markers; OBS 30.2+) and fall back to
                // the plain ffmpeg muxer if this OBS build doesn't register mp4_output. The
                // output is already a working ffmpeg_muxer recorder at this point (constructed
                // that way, with the .mp4 path already set), so a failed SetFormat needs no
                // further fallback construction - just leave it as-is.
                bool useHybridMp4 = true;
                _output = new RecordingOutput("simple_output", videoOutputPath);
                try
                {
                    _output.SetFormat(RecordingFormat.HybridMp4);
                }
                catch (NotSupportedException)
                {
                    useHybridMp4 = false;
                }
                Log.Information($"Using recording output type: {(useHybridMp4 ? "mp4_output" : "ffmpeg_muxer")} (Hybrid MP4: {useHybridMp4})");
                _output.Update(s => s.Set("tracks", (long)recordTracksMask));

                _output.WithVideoEncoder(_videoEncoder);
                for (int t = 0; t < _audioEncoders.Count; t++)
                {
                    _output.WithAudioEncoder(_audioEncoders[t], track: t);
                }

                // Detect unexpected stops (e.g. disk full mid-recording) so we can notify the user
                _output.Stopped += OnOutputStopped;
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

            RecordingPreviewService.OnRecordingStarted((uint)eff.FrameRate);

            PlatformServices.Tray.SetRecording(true);

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

            int monitorIndex = ResolveSelectedMonitorIndex(warnIfNotFound: true);

#if WINDOWS
            var captureMethod = Settings.Instance.DisplayCaptureMethod switch
            {
                DisplayCaptureMethod.DXGI => MonitorCaptureMethod.DesktopDuplication,
                DisplayCaptureMethod.WGC => MonitorCaptureMethod.WindowsGraphicsCapture,
                _ => MonitorCaptureMethod.Auto
            };

            _displaySource = MonitorCapture.FromMonitor(monitorIndex, "display")
                .SetCaptureMethod(captureMethod);

            Log.Information($"Display capture added for monitor {monitorIndex} using {Settings.Instance.DisplayCaptureMethod} method");
#else
            // Linux: on X11 use OBS's xshm screen capture. The PipeWire desktop-portal source is
            // Wayland-oriented and yields black frames on X11, so only use it when actually on Wayland.
            bool isWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            if (isWayland)
            {
                _displaySource = MonitorCapture.FromMonitor(monitorIndex, "display");
                Log.Information($"Display capture added for monitor {monitorIndex} using PipeWire (portal)");
            }
            else
            {
                var xshm = new Source("xshm_input", "display");
                xshm.Update(s =>
                {
                    s.Set("screen", monitorIndex);
                    s.Set("show_cursor", true);
                });
                _displaySource = xshm;
                Log.Information($"Display capture added for screen {monitorIndex} using X11 (xshm)");
            }
#endif

            // Add to scene (display is behind game capture in layer order)
            _displayItem = _mainScene.AddSource(_displaySource);
        }

        /// <summary>
        /// Switches the live display capture to the selected monitor in place (keeping the source and its
        /// scene-item bounds), so a mid-recording monitor change has no gap. No-op if no display source is
        /// active (e.g. a game is hooked); the new monitor then applies on the next recording.
        /// </summary>
        public static void UpdateMonitorCapture()
        {
            if (_displaySource == null)
            {
                Log.Information("Monitor selection changed but no active display capture to update; it will apply on the next recording.");
                return;
            }

            int monitorIndex = ResolveSelectedMonitorIndex(warnIfNotFound: true);
            if (_displaySource is MonitorCapture monitorCapture)
            {
                monitorCapture.SetMonitor(monitorIndex);
                Log.Information($"Updated live display capture to monitor {monitorIndex}");
            }
            else
            {
                // xshm/other source types: no in-place monitor switch; applies on next recording.
                Log.Information("Monitor selection changed; will apply on the next recording.");
            }
        }

        /// <summary>
        /// Resolves the monitor index to capture from the selected display setting, falling back to the
        /// first monitor when no display is selected or the selected one can't be found.
        /// </summary>
        private static int ResolveSelectedMonitorIndex(bool warnIfNotFound)
        {
            if (Settings.Instance.SelectedDisplay == null)
                return 0;

            int? foundIndex = AppState.Instance.Displays
                .Select((d, i) => new { Display = d, Index = i })
                .Where(x => x.Display.DeviceId == Settings.Instance.SelectedDisplay?.DeviceId)
                .Select(x => (int?)x.Index)
                .FirstOrDefault();

            if (foundIndex.HasValue)
                return foundIndex.Value;

            if (warnIfNotFound)
                _ = MessageService.ShowModal("Display recording", "Could not find selected display. Defaulting to first automatically detected display.", "warning");

            return 0;
        }

        /// <summary>
        /// Resolves the device id of the display that will be captured, mirroring the selection
        /// AddMonitorCapture makes (the selected display if found, otherwise the first display).
        /// Used to decide whether to record in HDR.
        /// </summary>
#if WINDOWS
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
#endif

        /// <summary>
        /// Clamps b-frames to what the GPU's NVENC block supports for this codec, based on the
        /// obs-nvenc-test probe result. OBS defaults to 2 b-frames regardless of hardware, and
        /// support is per codec: the GTX 1650 (TU117) for example handles H.264 b-frames but not
        /// HEVC b-frames, making every encode fail with "B-frames not supported on the current HW" (#151).
        /// On Linux the NVENC probe is not run, so this is a no-op.
        /// </summary>
        private static void ApplyNvencBFrameLimit(ObsKit.NET.Core.Settings videoEncoderSettings, string encoderId)
        {
#if WINDOWS
            int? maxBFrames = NvencCapsService.GetMaxBFrames(encoderId);
            if (maxBFrames == null)
                return;

            int bf = Math.Min(2, maxBFrames.Value);
            videoEncoderSettings.Set("bf", bf);
            if (bf < 2)
                Log.Information($"NVENC b-frames limited to {bf} ({encoderId} supports max {maxBFrames} on this GPU)");
#endif
        }

        // The Linux GPU encoders are FFmpeg VAAPI (ffmpeg_vaapi_tex / hevc_ffmpeg_vaapi_tex / av1_...).
        private static bool IsVaapiEncoder(string encoderId) =>
            encoderId.Contains("vaapi", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Configures the FFmpeg VAAPI encoder (the Linux GPU path). Its settings schema differs from
        /// the NVENC/QSV/AMF/x264 family: an <em>integer</em> AVCodecContext "profile" (not the
        /// "high"/"main10" strings), the VBR ceiling read from "maxrate" (not "max_bitrate"), and only
        /// CBR/VBR/CQP rate control. Mismatched keys make libavcodec fail to open the codec with
        /// "Function not implemented".
        /// </summary>
        private static void ConfigureVaapiVideoEncoder(ObsKit.NET.Core.Settings s, string encoderId, EffectiveRecordingSettings eff)
        {
            // Integer AVCodecContext.profile values (libavcodec AV_PROFILE_*). OBS auto-upgrades HEVC
            // Main -> Main10 for P010, but we pick the right one up front anyway.
            const int H264_HIGH = 100, HEVC_MAIN = 1, HEVC_MAIN_10 = 2, AV1_MAIN = 0;
            string codec = EncoderInfo.Get(encoderId)?.Codec ?? "h264";
            int profile = codec switch
            {
                "hevc" => _isHdrRecording ? HEVC_MAIN_10 : HEVC_MAIN,
                "av1" => AV1_MAIN,
                _ => H264_HIGH,
            };
            s.Set("profile", profile);

            // VAAPI supports CBR / VBR / CQP; map the x264-only CRF mode onto CQP.
            string rc = eff.RateControl == "CRF" ? "CQP" : eff.RateControl;
            s.Set("rate_control", rc);

            switch (rc)
            {
                case "CBR":
                    s.Set("bitrate", eff.Bitrate * 1000);
                    break;
                case "VBR":
                    // "maxrate" is VAAPI's VBR ceiling (kbps); "bitrate" is the target.
                    s.Set("bitrate", eff.MinBitrate * 1000);
                    s.Set("maxrate", eff.MaxBitrate * 1000);
                    break;
                case "CQP":
                    s.Set("qp", eff.RateControl == "CRF" ? eff.CrfValue : eff.CqLevel);
                    break;
            }
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

                // Use the same effective recording mode that StartRecording used (per-game override aware),
                // falling back to the global setting if no recording is active.
                RecordingMode effectiveMode = _activeEffectiveSettings?.RecordingMode ?? Settings.Instance.RecordingMode;
                bool effectiveDiscard = _activeEffectiveSettings?.DiscardSessionsWithoutBookmarks ?? Settings.Instance.DiscardSessionsWithoutBookmarks;
                bool isReplayBufferMode = effectiveMode == RecordingMode.Buffer;
                bool isHybridMode = effectiveMode == RecordingMode.Hybrid;

                if (isReplayBufferMode && _bufferOutput != null)
                {
                    // Let an in-flight replay save finish before stopping the buffer.
                    await WaitForInFlightReplaySaveAsync();

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

                    PlatformServices.Tray.SetRecording(false);

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

                    PlatformServices.Tray.SetRecording(false);

                    Log.Information("Recording stopped and disposed.");

                    _ = GameIntegrationService.Shutdown();

                    // Might be null or empty if the recording failed to start
                    if (AppState.Instance.Recording != null && AppState.Instance.Recording.FilePath != null)
                    {
                        // Check if we should discard the session due to no manual bookmarks
                        bool hasManualBookmarks = AppState.Instance.Recording.Bookmarks.Any(b => b.Type == BookmarkType.Manual);
                        if (effectiveDiscard && !hasManualBookmarks)
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
                        // Let an in-flight replay save finish before stopping the buffer.
                        await WaitForInFlightReplaySaveAsync();

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

                    PlatformServices.Tray.SetRecording(false);

                    Log.Information("Hybrid: All outputs stopped and disposed.");

                    _ = GameIntegrationService.Shutdown();

                    if (AppState.Instance.Recording != null && AppState.Instance.Recording.FilePath != null)
                    {
                        // Check if we should discard the session due to no manual bookmarks
                        bool hasManualBookmarks = AppState.Instance.Recording.Bookmarks.Any(b => b.Type == BookmarkType.Manual);
                        if (effectiveDiscard && !hasManualBookmarks)
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
                _activeEffectiveSettings = null;

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

                // Switch output audio: mute desktop sources and unmute game/voice chat sources
                var audioOutputMode = Settings.Instance.AudioOutputMode;
                if (audioOutputMode != AudioOutputMode.All)
                {
                    foreach (var desktopSource in _desktopSources)
                    {
                        try { desktopSource.IsMuted = true; }
                        catch (Exception ex) { Log.Warning($"Failed to mute desktop source: {ex.Message}"); }
                    }
                    Log.Information("Muted desktop audio sources (game hooked, using capture_audio)");

                    foreach (var (voiceName, _, voiceSource) in _voiceChatSources)
                    {
                        try { voiceSource.IsMuted = false; Log.Information($"Unmuted {voiceName} audio source (game hooked)"); }
                        catch (Exception ex) { Log.Warning($"Failed to unmute {voiceName} source: {ex.Message}"); }
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

            // Switch output audio back: unmute desktop sources and mute voice chat sources
            var audioOutputMode = Settings.Instance.AudioOutputMode;
            if (audioOutputMode != AudioOutputMode.All)
            {
                foreach (var desktopSource in _desktopSources)
                {
                    try { desktopSource.IsMuted = false; }
                    catch (Exception ex) { Log.Warning($"Failed to unmute desktop source: {ex.Message}"); }
                }
                Log.Information("Unmuted desktop audio sources (game unhooked, falling back to desktop audio)");

                foreach (var (voiceName, _, voiceSource) in _voiceChatSources)
                {
                    try { voiceSource.IsMuted = true; Log.Information($"Muted {voiceName} audio source (game unhooked)"); }
                    catch (Exception ex) { Log.Warning($"Failed to mute {voiceName} source: {ex.Message}"); }
                }
            }
        }

        private static void OnReplaySaved(object? sender, ReplaySavedEventArgs e)
        {
            Log.Information("Replay buffer saved callback received");

            // ReplayBuffer.Saved resolves the path on the signal itself: get_last_replay only
            // returns a value when no mux is in flight, so this is the one moment it is
            // guaranteed to be this save's.
            string? path = e.Path;

            lock (_replaySaveLock)
            {
                if (_activeReplaySave == null || _activeReplaySave.Signal.Task.IsCompleted)
                {
                    // A save whose request was already abandoned finished late. Leave the file
                    // for the orphaned-file recovery scan; new saves may proceed again.
                    Log.Warning($"Replay 'saved' signal arrived with no pending request (file: {path ?? "unknown"}); leaving it for recovery.");
                    _previousSaveIndeterminate = false;
                    return;
                }

                if (string.IsNullOrEmpty(path))
                {
                    _activeReplaySave.FailureReason = "OBS reported the replay as saved but did not return its path.";
                    _activeReplaySave.Signal.TrySetResult(null);
                }
                else
                {
                    _activeReplaySave.Signal.TrySetResult(path);
                }
            }
        }

        /// <summary>
        /// Fires whenever an output stops. OBS reports Success for normal stops (including ones
        /// Segra initiates). Any other code means OBS stopped the output on its own (disk full,
        /// encoder error, etc.), so we tear down our state and notify the user.
        /// Runs on an OBS thread, so heavy work is dispatched off it.
        /// </summary>
        private static void OnOutputStopped(object? sender, OutputStoppedEventArgs e)
        {
            if (e.IsSuccess)
                return;

            var code = e.Code;

            // Segra already initiated the stop; the teardown is running, so don't double-handle.
            if (_isStoppingOrStopped)
            {
                Log.Warning($"Output stopped with code {code} while already stopping.");
                return;
            }

            // In hybrid mode both outputs can stop together (same drive), so only handle the first.
            if (Interlocked.CompareExchange(ref _unexpectedStopHandled, 1, 0) != 0)
            {
                Log.Warning($"Output stopped with code {code}; an unexpected stop is already being handled.");
                return;
            }

            // OBS only reports a coarse code (e.g. the MP4/ffmpeg muxer reports a full disk as
            // EncodeError), so the actual cause - "No space left on device", a path/permission
            // problem, an encoder error, etc. - lives only in this string. We surface it directly
            // rather than guessing from the code. e.LastError comes from the output that actually
            // fired this signal, straight from obs_output_get_last_error at the moment it stopped.
            string? lastError = e.LastError;

            Log.Error($"OBS stopped the recording output unexpectedly (code {code}); last error: {lastError ?? "(none)"}");
            _ = Task.Run(() => HandleUnexpectedOutputStop(code, lastError));
        }

        /// <summary>
        /// Notifies the user about an unexpected output stop with a Segra-friendly message,
        /// then brings Segra's recording state in line with OBS (which already tore the output down).
        /// </summary>
        private static async Task HandleUnexpectedOutputStop(ObsOutputStopCode code, string? lastError)
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
        private static (string Title, string Description) MapOutputStopToMessage(ObsOutputStopCode code, string? lastError)
        {
            string e = lastError ?? string.Empty;
            bool Has(string sub) => e.Contains(sub, StringComparison.OrdinalIgnoreCase);

            // Out of disk space: muxer subprocess stderr "Error writing to '<path>', No space left on device"
            if (code == ObsOutputStopCode.NoSpace || Has("No space left on device") || Has("ENOSPC"))
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
            if (code == ObsOutputStopCode.BadPath || Has("Unable to write to") || Has("Couldn't open") ||
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
            if (code == ObsOutputStopCode.HdrDisabled || Has("Rec. 2100") || Has("10bitUnsupported") ||
                Has("8bitUnsupportedHdr") || Has("HdrUnsupported"))
            {
                return ("Recording stopped: HDR not supported by encoder",
                    "The recording stopped because the selected encoder cannot record HDR. Update your graphics drivers, or switch to an encoder that supports HDR (such as HEVC or AV1), then start again.");
            }

            // Output settings not supported by the selected encoder/format
            if (code == ObsOutputStopCode.Unsupported)
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

            // Remaining encoder failures surface as EncodeError without a recognizable string
            if (code == ObsOutputStopCode.EncodeError)
            {
                return ("Recording stopped: encoder error",
                    "The video encoder failed while recording, so the recording was stopped. Update your graphics drivers or try a different encoder in settings, then start again.");
            }

            return ("Recording stopped unexpectedly",
                "Recording was stopped unexpectedly and the file may be incomplete. Check the log for more details.");
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

        private static Source? TryAddVoiceChatSource((string Name, string Window) app, bool muted)
        {
            try
            {
                var voiceSource = new Source("wasapi_process_output_capture", $"{app.Name} Audio");
                voiceSource.Update(s =>
                {
                    s.Set("window", app.Window);
                    s.Set("priority", 2); // WINDOW_PRIORITY_EXE
                });
                voiceSource.IsMuted = muted;
                _mainScene!.AddSource(voiceSource);
                _voiceChatSources.Add((app.Name, app.Window, voiceSource));
                Log.Information($"Added {app.Name} application audio capture source{(muted ? " (muted until game hooks)" : "")}");
                return voiceSource;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to create {app.Name} audio capture source: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Called by GameDetectionService's process watcher. Starts capturing a voice chat app
        /// that launches while a GameAndDiscord-mode recording is active.
        /// </summary>
        public static void OnVoiceChatAppStarted(string exePath)
        {
            try
            {
                if (Settings.Instance.AudioOutputMode != AudioOutputMode.GameAndDiscord) return;
                if (_mainScene == null || GameCaptureSource == null || _isStoppingOrStopped) return;

                string fileName = Path.GetFileName(exePath);
                foreach (var app in VoiceChatApps)
                {
                    string appExe = app.Window.Split(':')[^1];
                    if (!string.Equals(fileName, appExe, StringComparison.OrdinalIgnoreCase)) continue;
                    if (_voiceChatSources.Any(v => v.Window == app.Window)) return;

                    var voiceSource = TryAddVoiceChatSource(app, muted: !GameCaptureSource.IsHooked);
                    if (voiceSource != null)
                    {
                        try { voiceSource.AudioMixers = _voiceChatMixerMask; }
                        catch (Exception ex) { Log.Warning($"Failed to set mixer for {app.Name} source: {ex.Message}"); }
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to handle voice chat app start for {exePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Repoints the game capture source at a newly launched game executable. Safe to call during
        /// teardown: the source may be disposed/nulled on another thread, so it is guarded and captured once.
        /// </summary>
        public static void UpdateGameCaptureWindow(string exePath)
        {
            try
            {
                if (_isStoppingOrStopped) return;

                var source = GameCaptureSource;
                if (source == null) return;

                string fileName = Path.GetFileName(exePath);
                source.Update(s => s.Set("window", $"*:*:{fileName}"));
                Log.Information($"Updated game capture source to: {fileName}");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to update game capture window for {exePath}: {ex.Message}");
            }
        }

        public static void DisposeSources()
        {
            // Dispose these first, while the scene is still alive, so SceneItem.Remove() can run
            // (the helpers no-op once _displayItem/_gameCaptureItem are null).
            DisposeDisplaySource();
            DisposeGameCaptureSource();

            if (_mainScene != null)
            {
                try
                {
                    if (Obs.IsInitialized)
                        Obs.ClearOutputSource(0);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to clear output channel: {ex.Message}");
                }

                try
                {
                    // Remove() is required, not just Dispose(): OBS's main canvas holds a strong
                    // reference to the scene, so without it the scene (and every audio source still
                    // parented to it) leaks and accumulates across recordings.
                    _mainScene.Remove();
                    _mainScene.Dispose();
                    Log.Information("Scene disposed");
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to dispose scene: {ex.Message}");
                }
                _mainScene = null;
            }

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

            // Dispose voice chat audio sources
            foreach (var (voiceName, _, voiceSource) in _voiceChatSources)
            {
                try
                {
                    voiceSource.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to dispose {voiceName} audio source: {ex.Message}");
                }
            }
            _voiceChatSources.Clear();
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
            // Use the active recording's effective settings (per-game override aware) when present,
            // falling back to the global settings for the pre-start check / when nothing is recording.
            string rateControl = _activeEffectiveSettings?.RateControl ?? Settings.Instance.RateControl;
            int bitrate = _activeEffectiveSettings?.Bitrate ?? Settings.Instance.Bitrate;
            int maxBitrate = _activeEffectiveSettings?.MaxBitrate ?? Settings.Instance.MaxBitrate;

            int videoMbps = rateControl switch
            {
                "CBR" => bitrate,
                "VBR" => maxBitrate,
                // CRF/CQP are quality-based with no explicit cap; assume a high worst case.
                _ => Math.Max(maxBitrate, QualityModeAssumedMbps)
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
                var driveSpace = StorageService.GetContentDriveSpaceGb();
                AppState.Instance.SetRecordingDriveSpaceGb(
                    driveSpace?.UsedGb,
                    driveSpace?.FreeGb,
                    sendToFrontend: true
                );

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
                        // Segra-initiated graceful stop: OBS finalizes the file, then Output.Stopped
                        // fires with ObsOutputStopCode.Success and is ignored by OnOutputStopped.
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
        /// Clears encoder references. Encoders are manually disposed since AutoDispose is false.
        /// </summary>
        public static void DisposeEncoders()
        {
            try { _videoEncoder?.Dispose(); } catch (Exception ex) { Log.Warning($"Error disposing video encoder: {ex.Message}"); }
            _videoEncoder = null;

            foreach (var audioEncoder in _audioEncoders)
            {
                try { audioEncoder.Dispose(); } catch (Exception ex) { Log.Warning($"Error disposing audio encoder: {ex.Message}"); }
            }
            _audioEncoders.Clear();
        }

        /// <summary>
        /// Clears output references. Outputs are manually disposed since AutoDispose is false;
        /// disposing an Output/ReplayBuffer also disconnects its Stopped/Saved subscriptions.
        /// </summary>
        public static void DisposeOutput()
        {
            // The 'saved' signal cannot be delivered past this point; fail any pending save so
            // its waiter doesn't sit out the backstop. If OBS still completes the file during
            // disposal, the orphaned-file recovery scan picks it up. Disposing the output also
            // joins any in-flight mux thread, so nothing stays unresolved on the OBS side.
            FailActiveReplaySave("The recording stopped before the replay finished saving. If the file completed, it can be recovered when Segra restarts.");
            lock (_replaySaveLock)
                _previousSaveIndeterminate = false;

            try { _output?.Dispose(); } catch (Exception ex) { Log.Warning($"Error disposing output: {ex.Message}"); }
            _output = null;

            try { _bufferOutput?.Dispose(); } catch (Exception ex) { Log.Warning($"Error disposing buffer output: {ex.Message}"); }
            _bufferOutput = null;
        }

        // ?isLinux=true selects the Linux recorder bundles; the default serves the Windows OBS zips.
#if WINDOWS
        private const string ObsVersionsUrl = "https://segra.tv/api/obs/versions";
#else
        private const string ObsVersionsUrl = "https://segra.tv/api/obs/versions?isLinux=true";
#endif

        public static async Task AvailableOBSVersionsAsync()
        {
            try
            {
                // SEGRA_OBS_VERSIONS_URL overrides the endpoint (useful for staging / local testing).
                string url = Environment.GetEnvironmentVariable("SEGRA_OBS_VERSIONS_URL") ?? ObsVersionsUrl;
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
                            response = [];
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error parsing OBS versions from API: {ex.Message}");
                        response = [];
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

                SettingsService.SetAvailableOBSVersions(response ?? []);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to get available OBS versions: {ex.Message}");
            }
        }

        public static bool IsOBSInstalled()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
#if WINDOWS
            return File.Exists(Path.Combine(baseDir, "obs.dll"));
#else
            // The launcher / re-exec resolves a runtime and exports SEGRA_OBS_DATA_PATH when it
            // succeeds; treat that as installed. Otherwise detect a downloaded bundle, a bundled
            // libobs, or a system obs-studio install.
            string? dataPath = Environment.GetEnvironmentVariable("SEGRA_OBS_DATA_PATH");
            if (!string.IsNullOrEmpty(dataPath) && Directory.Exists(dataPath))
                return true;

            return File.Exists(Path.Combine(Platform.Linux.LinuxObsRuntime.DownloadedBundleDir(), "lib", "libobs.so.0"))
                || File.Exists(Path.Combine(baseDir, "lib", "libobs.so.0"))
                || File.Exists(Path.Combine(baseDir, "libobs.so.0"))
                || LinuxSystemLibObsPath() != null;
#endif
        }

#if !WINDOWS
        // Downloads the Linux recorder bundle from the API, extracts it, and re-execs to apply it.
        // Expects OBSVersion.Url to be a direct .tar.gz or .zip URL.
        private static async Task DownloadLinuxObsRuntimeAsync()
        {
            if (AppState.Instance.AvailableOBSVersions == null || AppState.Instance.AvailableOBSVersions.Count == 0)
                await AvailableOBSVersionsAsync();

            var versions = AppState.Instance.AvailableOBSVersions;
            if (versions == null || versions.Count == 0)
            {
                Log.Error("No Linux OBS runtime bundles available from the API.");
                throw new Exception("linux-obs-unavailable");
            }

            string? selectedVersion = Settings.Instance.SelectedOBSVersion;
            var versionToDownload = (!string.IsNullOrEmpty(selectedVersion)
                    ? versions.FirstOrDefault(v => v.Version == selectedVersion) : null)
                ?? versions.Where(v => !v.IsBeta).OrderByDescending(v => v.Version).FirstOrDefault()
                ?? versions.First();

            string url = versionToDownload.Url;

            // The versions API serves a GitHub contents-API URL (JSON metadata, not the file); resolve the
            // real download_url from it via the same helper the Windows flow uses. A direct .tar.gz/.zip URL
            // (e.g. the mock/staging server) is used as-is.
            if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase)
                && url.Contains("/contents/", StringComparison.OrdinalIgnoreCase))
            {
                using var metaClient = new HttpClient();
                url = (await FetchGitHubFileMetadataAsync(metaClient, url, versionToDownload.Version)).DownloadUrl;
            }

            Log.Information($"Downloading Linux OBS runtime {versionToDownload.Version} from {url}");

            string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");
            Directory.CreateDirectory(appDataDir);
            bool isZip = url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            string archivePath = Path.Combine(appDataDir, isZip ? "obs-linux-download.zip" : "obs-linux-download.tar.gz");

            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = Timeout.InfiniteTimeSpan;
                using var resp = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                long totalBytes = resp.Content.Headers.ContentLength ?? -1L;
                using var contentStream = await resp.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                var buffer = new byte[8192];
                long totalRead = 0; int bytesRead, lastProgress = -1;
                while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalRead += bytesRead;
                    if (totalBytes > 0)
                    {
                        int progress = (int)((totalRead * 100) / totalBytes);
                        if (progress != lastProgress)
                        {
                            lastProgress = progress;
                            await SendFrontendMessage("ObsDownloadProgress", new { progress, status = "downloading" });
                        }
                    }
                }
            }

            Log.Information("Download complete; extracting Linux OBS runtime...");
            string dest = Platform.Linux.LinuxObsRuntime.DownloadedBundleDir();
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);

            if (isZip)
                ZipFile.ExtractToDirectory(archivePath, dest, overwriteFiles: true);
            else
            {
                using var fs = File.OpenRead(archivePath);
                using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                System.Formats.Tar.TarFile.ExtractToDirectory(gz, dest, overwriteFiles: true);
            }

            FlattenSingleTopDir(dest);
            EnsureExecutable(Path.Combine(dest, "bin", "ffmpeg"));
            EnsureExecutable(Path.Combine(dest, "ffmpeg"));
            try { File.Delete(archivePath); } catch { /* ignore */ }

            if (!File.Exists(Path.Combine(dest, "lib", "libobs.so.0")))
            {
                Log.Error("Downloaded Linux OBS bundle has no lib/libobs.so.0 (unexpected layout).");
                throw new Exception("linux-obs-bad-bundle");
            }

            Log.Information($"Linux OBS runtime ready at {dest}; restarting to apply.");
            await ShowModal("Recorder ready", "The recorder finished downloading. Segra will restart to apply it.", "info");
            await Task.Delay(500);

            // Re-exec so LD_LIBRARY_PATH / PATH / GStreamer plugin path pick up the new runtime.
            Platform.Linux.LinuxObsRuntime.ConfigureAndReexecIfNeeded();
        }

        private static void EnsureExecutable(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch { /* best effort */ }
        }

        // If the archive extracted everything under a single top-level folder, move it up so lib/ is at root.
        private static void FlattenSingleTopDir(string dest)
        {
            if (File.Exists(Path.Combine(dest, "lib", "libobs.so.0"))) return;
            var subdirs = Directory.GetDirectories(dest);
            var files = Directory.GetFiles(dest);
            if (subdirs.Length == 1 && files.Length == 0)
            {
                string inner = subdirs[0];
                foreach (var e in Directory.GetFileSystemEntries(inner))
                    Directory.Move(e, Path.Combine(dest, Path.GetFileName(e)));
                Directory.Delete(inner, true);
            }
        }

        // Locate a system-installed libobs (obs-studio package) across common library directories.
        private static string? LinuxSystemLibObsPath()
        {
            string[] dirs =
            {
                "/usr/lib/x86_64-linux-gnu",
                "/usr/lib64",
                "/usr/lib",
                "/usr/local/lib",
                "/usr/local/lib/x86_64-linux-gnu",
            };
            foreach (var d in dirs)
            {
                var p = Path.Combine(d, "libobs.so.0");
                if (File.Exists(p)) return p;
            }
            return null;
        }
#endif

        public static async Task CheckIfExistsOrDownloadAsync(bool isUpdate = false)
        {
            Log.Information("Checking if OBS is installed");

#if !WINDOWS
            // Linux: use an already-resolved runtime (downloaded/bundled/system), else download the bundle.
            if (IsOBSInstalled())
            {
                Log.Information("OBS runtime found (downloaded, bundled, or system)");
                _ = AvailableOBSVersionsAsync();
                return;
            }

            await DownloadLinuxObsRuntimeAsync();
#else
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

                    // Fetch the metadata from GitHub to resolve the real download URL + hash.
                    var metadata = await FetchGitHubFileMetadataAsync(httpClient, metadataUrl, versionToDownload.Version);
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
#endif
        }

        // Resolves a GitHub contents-API URL to the file's metadata (download_url + sha). Shared by the
        // Windows OBS zip download and the Linux runtime download.
        private static async Task<GitHubFileMetadata> FetchGitHubFileMetadataAsync(HttpClient client, string metadataUrl, string versionLabel)
        {
            client.DefaultRequestHeaders.UserAgent.TryParseAdd("Segra");
            client.DefaultRequestHeaders.Accept.TryParseAdd("application/vnd.github.v3.json");

            Log.Information($"Fetching metadata for OBS version {versionLabel} from {metadataUrl}");
            var response = await client.GetAsync(metadataUrl);
            if (!response.IsSuccessStatusCode)
            {
                Log.Error($"Failed to fetch metadata from {metadataUrl}. Status: {response.StatusCode}");
                throw new Exception($"Failed to fetch file metadata: {response.ReasonPhrase}");
            }

            var metadata = System.Text.Json.JsonSerializer.Deserialize<GitHubFileMetadata>(
                await response.Content.ReadAsStringAsync());
            if (metadata?.DownloadUrl == null)
            {
                Log.Error("Download URL not found in the API response.");
                throw new Exception("Invalid API response: Missing download URL.");
            }
            return metadata;
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

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            PlatformServices.Sound.Play(ms.ToArray(), Settings.Instance.SoundEffectsVolume);
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

                // ── VAAPI (Linux hardware) ─────────────────────────
                // The texture ('_tex') variants carry OBS_ENCODER_CAP_PASS_TEXTURE, so
                // EncoderInfo reports IsHardware=true; the non-tex variants are internal
                // (software) and are intentionally left out so they don't appear under GPU.
                ["ffmpeg_vaapi_tex"] = "VAAPI H.264",
                ["hevc_ffmpeg_vaapi_tex"] = "VAAPI H.265",
                ["av1_ffmpeg_vaapi_tex"] = "VAAPI AV1",

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

            // Several of our curated encoder ids (e.g. jim_nvenc, obs_qsv11_v2) are marked
            // deprecated/internal by libobs despite being the ones we actually use, so this
            // must stay a per-id EncoderInfo lookup rather than EncoderInfo.GetVideoEncoders()
            // (which filters those out). Look up once via a map instead of once per encoder -
            // EncoderInfo.Get() re-enumerates every registered encoder internally.
            var encoderInfoById = EncoderInfo.GetAll(includeInternal: true)
                .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var encoderId in encoderTypes)
            {
                EncoderFriendlyNames.TryGetValue(encoderId, out var name);
                string friendlyName = name ?? encoderId;
                bool isHardware = encoderInfoById.TryGetValue(encoderId, out var info) && info.IsHardware;

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

                // If not found, try VAAPI H.264 (Linux hardware)
                if (selectedCodec == null)
                {
                    selectedCodec = availableCodecs.FirstOrDefault(
                        c => c.InternalEncoderId.Equals(
                            "ffmpeg_vaapi_tex",
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
