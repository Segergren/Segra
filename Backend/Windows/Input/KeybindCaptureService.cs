using Serilog;
using NAudio.Wave;
using Segra.Backend.App;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using Segra.Backend.Recorder;
using Segra.Backend.Core.Models;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;

namespace Segra.Backend.Windows.Input
{
    internal class KeybindCaptureService
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_CONTROL = 0x11;
        private const int VK_ALT = 0x12;
        private const int VK_SHIFT = 0x10;
        private const int KEY_PRESSED_MASK = 0x8000;

        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;
        private static List<Keybind>? _cachedKeybindings;
        private static HashSet<int>? _boundMainKeys;

        public static void Start()
        {
            RefreshKeybindingsCache();
            _hookID = SetHook(_proc);
            Application.Run();
        }

        public static void Stop()
        {
            UnhookWindowsHookEx(_hookID);
        }

        public static void RefreshKeybindingsCache()
        {
            var keybindings = Settings.Instance.Keybindings?.Where(k => k.Enabled).ToList();
            _cachedKeybindings = keybindings;

            if (keybindings != null && keybindings.Count > 0)
            {
                _boundMainKeys = new HashSet<int>();
                foreach (var kb in keybindings)
                {
                    foreach (var key in kb.Keys)
                    {
                        if (key != VK_CONTROL && key != VK_ALT && key != VK_SHIFT)
                        {
                            _boundMainKeys.Add(key);
                        }
                    }
                }
            }
            else
            {
                _boundMainKeys = null;
            }
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            ProcessModule curModule = Process.GetCurrentProcess().MainModule!;
            return SetWindowsHookEx(
                WH_KEYBOARD_LL,
                proc,
                GetModuleHandle(curModule.ModuleName),
                0
            );
        }

        private delegate IntPtr LowLevelKeyboardProc(
            int nCode, IntPtr wParam, IntPtr lParam);

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
            {
                var boundKeys = _boundMainKeys;
                if (boundKeys == null || boundKeys.Count == 0)
                {
                    return CallNextHookEx(_hookID, nCode, wParam, lParam);
                }

                int vkCode = Marshal.ReadInt32(lParam);

                if (!boundKeys.Contains(vkCode))
                {
                    return CallNextHookEx(_hookID, nCode, wParam, lParam);
                }

                var keybindings = _cachedKeybindings!;

                // Unrelated held keys (e.g. W while moving in a game) must not block a match,
                // but when both F8 and Ctrl+F8 are bound, Ctrl+F8 should only fire the latter.
                int maxMatchedKeyCount = 0;
                foreach (var keybind in keybindings)
                {
                    if (DoKeysMatch(keybind.Keys, vkCode) && keybind.Keys.Count > maxMatchedKeyCount)
                    {
                        maxMatchedKeyCount = keybind.Keys.Count;
                    }
                }

                foreach (var keybind in keybindings)
                {
                    if (keybind.Keys.Count == maxMatchedKeyCount && DoKeysMatch(keybind.Keys, vkCode))
                    {
                        HandleKeybindAction(keybind.Action);
                    }
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private static bool DoKeysMatch(List<int> keybindKeys, int triggerVkCode)
        {
            bool containsTrigger = false;
            foreach (var key in keybindKeys)
            {
                if (key == triggerVkCode)
                {
                    containsTrigger = true;
                    continue;
                }

                // GetAsyncKeyState reflects the physical state; GetKeyState is stale on
                // this thread because it never receives keyboard messages.
                if ((GetAsyncKeyState(key) & KEY_PRESSED_MASK) == 0)
                    return false;
            }

            return containsTrigger;
        }

        private static void HandleKeybindAction(KeybindAction action)
        {
            var recording = AppState.Instance.Recording;
            var preRecording = AppState.Instance.PreRecording;
            var recordingMode = Settings.Instance.RecordingMode;

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
                        _ = MessageService.SendFrontendMessage("ReplayBufferSaved", new { });
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
            using var audioStream = new MemoryStream(Properties.Resources.bookmark);
            using var audioReader = new WaveFileReader(audioStream);
            var sampleProvider = audioReader.ToSampleProvider();
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

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook,
            LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk,
            int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int nVirtKey);
    }
}
