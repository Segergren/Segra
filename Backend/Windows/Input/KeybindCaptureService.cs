using NAudio.Wave;
using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Recorder;
using Serilog;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Segra.Backend.Windows.Input
{
    internal class KeybindCaptureService
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_CONTROL = 0x11;
        private const int VK_ALT = 0x12;
        private const int VK_SHIFT = 0x10;
        private const int KEY_PRESSED_MASK = 0x8000;

        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;
        private static List<Keybind>? _cachedKeybindings;
        private static HashSet<int>? _boundMainKeys;
        private static readonly int[] _pressedKeys = new int[4];

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
            if (nCode >= 0 && wParam == WM_KEYDOWN)
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

                bool ctrlPressed = (GetKeyState(VK_CONTROL) & KEY_PRESSED_MASK) != 0;
                bool altPressed = (GetKeyState(VK_ALT) & KEY_PRESSED_MASK) != 0;
                bool shiftPressed = (GetKeyState(VK_SHIFT) & KEY_PRESSED_MASK) != 0;

                int pressedCount = 0;
                if (ctrlPressed) _pressedKeys[pressedCount++] = VK_CONTROL;
                if (altPressed) _pressedKeys[pressedCount++] = VK_ALT;
                if (shiftPressed) _pressedKeys[pressedCount++] = VK_SHIFT;
                _pressedKeys[pressedCount++] = vkCode;

                var keybindings = _cachedKeybindings!;
                foreach (var keybind in keybindings)
                {
                    if (DoKeysMatch(keybind.Keys, pressedCount))
                    {
                        var recording = Settings.Instance.State.Recording;
                        if (recording == null)
                        {
                            return CallNextHookEx(_hookID, nCode, wParam, lParam);
                        }

                        HandleKeybindAction(keybind.Action, recording);
                    }
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private static bool DoKeysMatch(List<int> keybindKeys, int pressedCount)
        {
            if (keybindKeys.Count != pressedCount)
                return false;

            foreach (var key in keybindKeys)
            {
                bool found = false;
                for (int i = 0; i < pressedCount; i++)
                {
                    if (_pressedKeys[i] == key)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) return false;
            }

            return true;
        }

        private static void HandleKeybindAction(KeybindAction action, Recording recording)
        {
            var recordingMode = Settings.Instance.RecordingMode;

            switch (action)
            {
                case KeybindAction.CreateBookmark:
                    if (recording != null && (recordingMode == RecordingMode.Session || recordingMode == RecordingMode.Hybrid))
                    {
                        Log.Information("Saving bookmark...");
                        recording.Bookmarks.Add(new Bookmark
                        {
                            Type = BookmarkType.Manual,
                            Time = DateTime.Now - recording.StartTime
                        });
                        Task.Run(PlayBookmarkSound);
                        _ = MessageService.SendFrontendMessage("BookmarkCreated", new { });
                    }
                    break;

                case KeybindAction.SaveReplayBuffer:
                    if (recording != null && (recordingMode == RecordingMode.Buffer || recordingMode == RecordingMode.Hybrid))
                    {
                        Log.Information("Saving replay buffer...");
                        Task.Run(OBSService.SaveReplayBuffer);
                        Task.Run(PlayBookmarkSound);
                    }
                    break;
            }
        }

        private static void PlayBookmarkSound()
        {
            var audioStream = new MemoryStream(Properties.Resources.bookmark);
            var audioReader = new WaveFileReader(audioStream);
            var waveOut = new WaveOutEvent();

            var volumeStream = new VolumeWaveProvider16(audioReader)
            {
                Volume = 0.5f
            };

            waveOut.Init(volumeStream);

            waveOut.PlaybackStopped += (sender, args) =>
            {
                waveOut.Dispose();
                audioReader.Dispose();
                audioStream.Dispose();
            };

            waveOut.Play();
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
        private static extern short GetKeyState(int nVirtKey);
    }
}
