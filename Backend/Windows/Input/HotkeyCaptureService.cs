using Serilog;
using ObsKit.NET;
using ObsKit.NET.Hotkeys;
using Segra.Backend.App;
using Segra.Backend.Platform;
using Segra.Backend.Recorder;
using Segra.Backend.Core.Models;
using ObsKit.NET.Native.Types;
using ObsKeys = ObsKit.NET.Core.ObsKeys;
#if WINDOWS
using Segra.Backend.Windows.Input.HotkeyBroker;
#endif

namespace Segra.Backend.Windows.Input
{
    /// <summary>
    /// Registers Segra's user-configurable hotkeys as OBS hotkeys via ObsKit.NET.
    /// libobs polls global key state on its own background thread, so bound combinations
    /// fire system-wide with no OS hook of our own. Hotkeys can only be registered once
    /// OBS is initialized, so <see cref="Start"/> must be called from
    /// <see cref="OBSService.InitializeAsync"/> (after Obs.Initialize succeeds), not at
    /// app launch, and <see cref="Stop"/> from <see cref="OBSService.Shutdown"/>.
    /// </summary>
    internal class HotkeyCaptureService
    {
        // VK codes for the modifier keys the frontend lets users combine with a main key.
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_ALT = 0x12; // VK_MENU
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        // Guards _registered, _brokerClient, _brokerActive and _waylandSource so the hotkey sources never overlap.
        private static readonly object _lock = new();
        private static readonly List<RegisteredHotkey> _registered = [];
#if WINDOWS
        private static HotkeyBrokerClient? _brokerClient;
        private static bool _brokerActive;
#else
        // libobs never sees keys pressed in other apps on Wayland, so those come from input devices or XWayland instead
        private static Platform.Linux.ILinuxHotkeySource? _waylandSource;
#endif

        /// <summary>
        /// Starts hotkey capture. Prefers the privileged <c>Segra.Hotkeys</c> broker (which can read
        /// input over elevated games, where OBS's own hotkeys cannot); falls back to the OBS hotkey
        /// system when the broker is not installed or not reachable. Call once OBS is initialized.
        /// </summary>
        public static void Start()
        {
#if WINDOWS && !DEBUG && !LOCAL_BUILD
            var client = new HotkeyBrokerClient();
            client.StateChanged += OnBrokerStateChanged;
            client.ActionFired += HandleHotkeyAction;
            lock (_lock)
                _brokerClient = client;
            client.Start();
            PublishBrokerStatus();
#endif
#if !WINDOWS
            // On X11 sessions libobs's own hotkeys already see every key
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                // XWayland only sees keys while an X11 window has focus, so it is the fallback
                Platform.Linux.ILinuxHotkeySource? source = Platform.Linux.EvdevHotkeyPoller.TryStart(HandleHotkeyAction, out bool permissionDenied);
                AppState.Instance.HotkeysNeedInputGroup = permissionDenied;
                if (source == null)
                {
                    if (permissionDenied)
                        Log.Warning("No permission to read keyboards in /dev/input, so hotkeys only work while an X11 window (e.g. a Wine/Proton game) is focused. " +
                            "Add your user to the 'input' group to make them work everywhere: sudo usermod -aG input $USER, then log out and back in");

                    source = Platform.Linux.XWaylandHotkeyPoller.TryStart(HandleHotkeyAction);
                    if (source != null)
                        Log.Information("Reading hotkeys from XWayland");
                    else
                        Log.Warning("No readable keyboards and no X server reachable, so hotkeys can't work on this Wayland session");
                }

                lock (_lock)
                    _waylandSource = source;
            }
#endif
            RefreshHotkeysCache();
        }

        /// <summary>
        /// Unregisters all hotkeys and stops the broker client. Call before/at OBS shutdown.
        /// </summary>
        public static void Stop()
        {
#if WINDOWS
            HotkeyBrokerClient? client;
#else
            Platform.Linux.ILinuxHotkeySource? source;
#endif
            lock (_lock)
            {
#if WINDOWS
                client = _brokerClient;
                _brokerClient = null;
                _brokerActive = false;
#else
                source = _waylandSource;
                _waylandSource = null;
#endif
                ClearRegisteredHotkeys();
            }

#if !WINDOWS
            source?.Dispose();
#endif
#if WINDOWS
            if (client is null)
                return;

            client.StateChanged -= OnBrokerStateChanged;
            client.ActionFired -= HandleHotkeyAction;
            client.Dispose();
#endif
        }

#if WINDOWS
        /// <summary>
        /// Stops the broker instead of leaving it to idle out. Pass <paramref name="exitTimeout"/> to
        /// wait for it, which updates need because it can pin the install directory.
        /// </summary>
        public static void ShutdownBroker(TimeSpan? exitTimeout = null)
        {
            HotkeyBrokerClient? client;
            lock (_lock)
            {
                client = _brokerClient;
                _brokerClient = null;
                _brokerActive = false;
            }

            if (client is not null)
            {
                client.StateChanged -= OnBrokerStateChanged;
                client.ActionFired -= HandleHotkeyAction;
                client.Dispose();
            }

            HotkeyBrokerShutdown.RequestShutdown(exitTimeout);
        }
#endif

        /// <summary>
        /// Re-applies the current hotkeys to whichever source is active. Call whenever
        /// <c>Settings.Instance.Hotkeys</c> changes.
        /// </summary>
        public static void RefreshHotkeysCache()
        {
            lock (_lock)
            {
                var hotkeys = Settings.Instance.Hotkeys?.Where(k => k.Enabled).ToList() ?? [];

#if WINDOWS
                // The broker is the sole source while connected so a press is never delivered twice.
                if (_brokerActive)
                {
                    ClearRegisteredHotkeys();
                    _brokerClient?.UpdateHotkeys(hotkeys);
                    return;
                }
#else
                // The Wayland source is the sole source while it runs; libobs falls back to X11 hotkeys when Wayland is unreachable, which would fire twice
                if (_waylandSource != null)
                {
                    ClearRegisteredHotkeys();
                    _waylandSource.SetBindings(hotkeys);
                    return;
                }
#endif

                RegisterObsHotkeys(hotkeys);
            }
        }

#if WINDOWS
        private static void OnBrokerStateChanged()
        {
            lock (_lock)
            {
                if (_brokerClient is null)
                    return;

                _brokerActive = _brokerClient.IsActive;
                RefreshHotkeysCache();
            }

            PublishBrokerStatus();
        }

        /// <summary>
        /// Publishes the broker's install and connection state to the frontend and lets
        /// <see cref="HotkeyBrokerSetup"/> start the automatic install when one is due.
        /// </summary>
        public static void PublishBrokerStatus()
        {
            bool connected;
            bool rejected;
            lock (_lock)
            {
                connected = _brokerActive;
                rejected = _brokerClient?.HasRejectedInstall ?? false;
            }

            HotkeyBrokerSetup.Refresh(connected, rejected);
        }
#endif

        // Callers hold _lock.
        private static void ClearRegisteredHotkeys()
        {
            foreach (var hotkey in _registered)
                hotkey.Dispose();
            _registered.Clear();
        }

        // Callers hold _lock.
        private static void RegisterObsHotkeys(List<Hotkey> hotkeys)
        {
            if (!OBSService.IsInitialized)
            {
                Log.Information("Hotkeys changed before OBS initialization; will apply once OBS starts.");
                return;
            }

            ClearRegisteredHotkeys();

            foreach (var hotkey in hotkeys)
            {
                if (!TryBuildCombination(hotkey.Keys, out var combination))
                {
                    Log.Warning($"Skipping hotkey for {hotkey.Action}: only one non-modifier key plus Ctrl/Alt/Shift/Win is supported.");
                    continue;
                }

                try
                {
                    var obsHotkey = Obs.RegisterHotkey($"segra_{hotkey.Action}", hotkey.Action.ToString(), pressed =>
                    {
                        if (pressed)
                            HandleHotkeyAction(hotkey.Action);
                    });
                    obsHotkey.Bind(combination);
                    _registered.Add(obsHotkey);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"Failed to register hotkey for {hotkey.Action}");
                }
            }
        }

        /// <summary>
        /// Converts a hotkey's raw Win32 virtual-key codes into an OBS key combination.
        /// Segra's hotkey model allows any set of VK codes; ObsKeyCombination supports at
        /// most one non-modifier key plus Ctrl/Alt/Shift/Win, so combinations with more than
        /// one non-modifier key are rejected (unsupported by design, not silently dropped).
        /// </summary>
        private static bool TryBuildCombination(List<int> keys, out ObsKeyCombination combination)
        {
            combination = default;
            var modifiers = ObsKeyModifiers.None;
            ObsKey? mainKey = null;

            foreach (var vk in keys)
            {
                switch (vk)
                {
                    case VK_CONTROL:
                        modifiers |= ObsKeyModifiers.Control;
                        break;
                    case VK_ALT:
                        modifiers |= ObsKeyModifiers.Alt;
                        break;
                    case VK_SHIFT:
                        modifiers |= ObsKeyModifiers.Shift;
                        break;
                    case VK_LWIN:
                    case VK_RWIN:
                        modifiers |= ObsKeyModifiers.Command;
                        break;
                    default:
                        var key = ObsKeys.FromWindowsVirtualKey(vk);
                        if (key == ObsKey.None)
                            return false;
                        if (mainKey != null && mainKey != key)
                            return false; // more than one non-modifier key: unsupported
                        mainKey = key;
                        break;
                }
            }

            if (mainKey == null)
            {
                // No non-modifier key (e.g. a lone "Win" binding, which the frontend allows -
                // it only excludes Shift/Ctrl/Alt from becoming the main key, not Win).
                // ObsKeyCombination supports modifier-only combinations via ObsKey.None.
                if (modifiers == ObsKeyModifiers.None)
                    return false;

                combination = new ObsKeyCombination(ObsKey.None, modifiers);
                return true;
            }

            combination = new ObsKeyCombination(mainKey.Value, modifiers);
            return true;
        }

        // Paused while the settings screen records a binding; expires in case the frontend never resumes
        private static long _pausedUntilTicks;
        private static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(1);

        public static void SetPaused(bool paused)
        {
            Volatile.Write(ref _pausedUntilTicks, paused ? DateTime.UtcNow.Add(MaxPause).Ticks : 0);
            Log.Information(paused ? "Hotkeys paused while a hotkey is being rebound" : "Hotkeys resumed");
        }

        private static void HandleHotkeyAction(HotkeyAction action)
        {
            if (DateTime.UtcNow.Ticks < Volatile.Read(ref _pausedUntilTicks))
            {
                Log.Information($"Ignoring hotkey {action} while a hotkey is being rebound");
                return;
            }

            var recording = AppState.Instance.Recording;
            var preRecording = AppState.Instance.PreRecording;
            // Use the active recording's effective mode (per-game override aware) so bookmark/replay
            // hotkeys behave according to the mode the current recording actually started in.
            var recordingMode = OBSService.ActiveEffectiveSettings?.RecordingMode ?? Settings.Instance.RecordingMode;

            switch (action)
            {
                case HotkeyAction.CreateBookmark:
                    if (recording != null && (recordingMode == RecordingMode.Session || recordingMode == RecordingMode.Hybrid))
                    {
                        Log.Information("Saving bookmark...");
                        var bookmark = new Bookmark
                        {
                            Type = BookmarkType.Manual,
                            Time = DateTime.Now - recording.StartTime
                        };
                        recording.AddBookmark(bookmark);
                        Task.Run(PlayBookmarkSound);
                        _ = MessageService.SendFrontendMessage("BookmarkCreated", new { });
                    }
                    break;

                case HotkeyAction.SaveReplayBuffer:
                    if (OBSService.IsAlwaysOnBufferActive || (recording != null && (recordingMode == RecordingMode.Buffer || recordingMode == RecordingMode.Hybrid)))
                    {
                        Log.Information("Saving replay buffer...");
                        // Immediate keypress acknowledgment (sound + shockwave); the separate
                        // "ReplayBufferSaved" event is sent by SaveReplayBuffer once OBS
                        // confirms the file is actually written.
                        _ = MessageService.SendFrontendMessage("ReplayBufferSaveStarted", new { });
                        Task.Run(OBSService.SaveReplayBuffer);
                        Task.Run(PlayBookmarkSound);
                    }
                    break;

                case HotkeyAction.ToggleRecording:
                    if (recording != null || preRecording != null)
                    {
                        Log.Information("Hotkey: stopping recording");
                        Task.Run(() => OBSService.StopRecording());
                    }
                    else
                    {
                        Log.Information("Hotkey: starting display recording");
                        Task.Run(() => OBSService.StartRecording(startManually: true));
                    }
                    break;

                case HotkeyAction.TogglePreview:
                    if (recording != null)
                    {
                        Log.Information("Hotkey: toggling recording preview");
                        RecordingPreviewService.Toggle();
                    }
                    break;
            }
        }

        private static void PlayBookmarkSound()
        {
            PlatformServices.Sound.Play(Properties.Resources.bookmark, Settings.Instance.SoundEffectsVolume);
        }
    }
}
