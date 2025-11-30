using NAudio.Wave;
using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Obs;
using Serilog;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Segra.Backend.Windows.Input
{
    internal class KeybindCaptureService
    {
        // Windows hook type for low-level keyboard input events
        private const int WH_KEYBOARD_LL = 13;
        // Windows message for key down event
        private const int WM_KEYDOWN = 0x0100;
        // Delegate instance for the keyboard hook callback function
        private static LowLevelKeyboardProc _proc = HookCallback;
        // Handle to the installed keyboard hook
        private static IntPtr _hookID = IntPtr.Zero;

        public static void Start()
        {
            _hookID = SetHook(_proc);
            Application.Run();
        }

        public static void Stop()
        {
            UnhookWindowsHookEx(_hookID);
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

        private static readonly List<int> _pressedKeys = new List<int>(4);

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);

                bool ctrlPressed = (Control.ModifierKeys & Keys.Control) == Keys.Control;
                bool altPressed = (Control.ModifierKeys & Keys.Alt) == Keys.Alt;
                bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

                _pressedKeys.Clear();
                if (ctrlPressed) _pressedKeys.Add(17);
                if (altPressed) _pressedKeys.Add(18);
                if (shiftPressed) _pressedKeys.Add(16);
                _pressedKeys.Add(vkCode);

                var keybindings = Settings.Instance.Keybindings;
                if (keybindings != null)
                {
                    foreach (var keybind in keybindings)
                    {
                        if (keybind.Enabled && DoKeysMatch(keybind.Keys, _pressedKeys))
                        {
                            var recording = Settings.Instance.State.Recording;
                            if (recording == null)
                            {
                                return CallNextHookEx(_hookID, nCode, wParam, lParam);
                            }

                            switch (keybind.Action)
                            {
                                case KeybindAction.CreateBookmark:
                                    if (recording != null && (Settings.Instance.RecordingMode == RecordingMode.Session || Settings.Instance.RecordingMode == RecordingMode.Hybrid))
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
                                    if (recording != null && (Settings.Instance.RecordingMode == RecordingMode.Buffer || Settings.Instance.RecordingMode == RecordingMode.Hybrid))
                                    {
                                        Log.Information("Saving replay buffer...");
                                        Task.Run(OBSService.SaveReplayBuffer);
                                        Task.Run(PlayBookmarkSound);
                                    }
                                    break;
                            }
                        }
                    }
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private static bool DoKeysMatch(List<int> keybindKeys, List<int> pressedKeys)
        {
            if (keybindKeys.Count != pressedKeys.Count)
                return false;

            foreach (var key in keybindKeys)
            {
                if (!pressedKeys.Contains(key))
                    return false;
            }

            return true;
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
    }
}
