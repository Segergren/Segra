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
    /// Registers Segra's user-configurable keybindings as OBS hotkeys via ObsKit.NET.
    /// libobs polls global key state on its own background thread, so bound combinations
    /// fire system-wide with no OS hook of our own. Hotkeys can only be registered once
    /// OBS is initialized, so <see cref="Start"/> must be called from
    /// <see cref="OBSService.InitializeAsync"/> (after Obs.Initialize succeeds), not at
    /// app launch, and <see cref="Stop"/> from <see cref="OBSService.Shutdown"/>.
    /// </summary>
    internal class KeybindCaptureService
    {
        // VK codes for the modifier keys the frontend lets users combine with a main key.
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_ALT = 0x12; // VK_MENU
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        // Guards _registered, _brokerClient and _brokerActive so the OBS and broker sources never overlap.
        private static readonly object _lock = new();
        private static readonly List<RegisteredHotkey> _registered = [];
#if WINDOWS
        private static HotkeyBrokerClient? _brokerClient;
        private static bool _brokerActive;
#endif

        /// <summary>
        /// Starts hotkey capture. Prefers the privileged <c>Segra.Hotkeys</c> broker (which can read
        /// input over elevated games, where OBS's own hotkeys cannot); falls back to the OBS hotkey
        /// system when the broker is not installed or not reachable. Call once OBS is initialized.
        /// </summary>
        public static void Start()
        {
#if WINDOWS
            var client = new HotkeyBrokerClient();
            client.StateChanged += OnBrokerStateChanged;
            client.ActionFired += HandleKeybindAction;
            lock (_lock)
                _brokerClient = client;
            client.Start();
            PublishBrokerStatus();
#endif
            RefreshKeybindingsCache();
        }

        /// <summary>
        /// Unregisters all hotkeys and stops the broker client. Call before/at OBS shutdown.
        /// </summary>
        public static void Stop()
        {
#if WINDOWS
            HotkeyBrokerClient? client;
#endif
            lock (_lock)
            {
#if WINDOWS
                client = _brokerClient;
                _brokerClient = null;
                _brokerActive = false;
#endif
                ClearRegisteredHotkeys();
            }

#if WINDOWS
            if (client is null)
                return;

            client.StateChanged -= OnBrokerStateChanged;
            client.ActionFired -= HandleKeybindAction;
            client.Dispose();
#endif
        }

        /// <summary>
        /// Re-applies the current keybindings to whichever source is active. Call whenever
        /// <c>Settings.Instance.Keybindings</c> changes.
        /// </summary>
        public static void RefreshKeybindingsCache()
        {
            lock (_lock)
            {
                var keybindings = Settings.Instance.Keybindings?.Where(k => k.Enabled).ToList() ?? [];

#if WINDOWS
                // The broker is the sole source while connected so a press is never delivered twice.
                if (_brokerActive)
                {
                    ClearRegisteredHotkeys();
                    _brokerClient?.UpdateKeybindings(keybindings);
                    return;
                }
#endif

                RegisterObsHotkeys(keybindings);
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
                RefreshKeybindingsCache();
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
        private static void RegisterObsHotkeys(List<Keybind> keybindings)
        {
            if (!OBSService.IsInitialized)
            {
                Log.Information("Keybindings changed before OBS initialization; will apply once OBS starts.");
                return;
            }

            ClearRegisteredHotkeys();

            foreach (var keybind in keybindings)
            {
                if (!TryBuildCombination(keybind.Keys, out var combination))
                {
                    Log.Warning($"Skipping keybind for {keybind.Action}: only one non-modifier key plus Ctrl/Alt/Shift/Win is supported.");
                    continue;
                }

                try
                {
                    var hotkey = Obs.RegisterHotkey($"segra_{keybind.Action}", keybind.Action.ToString(), pressed =>
                    {
                        if (pressed)
                            HandleKeybindAction(keybind.Action);
                    });
                    hotkey.Bind(combination);
                    _registered.Add(hotkey);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"Failed to register hotkey for {keybind.Action}");
                }
            }
        }

        /// <summary>
        /// Converts a keybind's raw Win32 virtual-key codes into an OBS key combination.
        /// Segra's keybind model allows any set of VK codes; ObsKeyCombination supports at
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

        private static void HandleKeybindAction(KeybindAction action)
        {
            var recording = AppState.Instance.Recording;
            var preRecording = AppState.Instance.PreRecording;
            // Use the active recording's effective mode (per-game override aware) so bookmark/replay
            // hotkeys behave according to the mode the current recording actually started in.
            var recordingMode = OBSService.ActiveEffectiveSettings?.RecordingMode ?? Settings.Instance.RecordingMode;

            switch (action)
            {
                case KeybindAction.CreateBookmark:
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

                case KeybindAction.SaveReplayBuffer:
                    if (recording != null && (recordingMode == RecordingMode.Buffer || recordingMode == RecordingMode.Hybrid))
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

                case KeybindAction.ToggleRecording:
                    if (recording != null || preRecording != null)
                    {
                        Log.Information("Hotkey: stopping recording");
                        Task.Run(OBSService.StopRecording);
                    }
                    else
                    {
                        Log.Information("Hotkey: starting display recording");
                        Task.Run(() => OBSService.StartRecording(startManually: true));
                    }
                    break;

                case KeybindAction.TogglePreview:
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
