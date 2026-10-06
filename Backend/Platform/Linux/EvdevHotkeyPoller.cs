using Serilog;
using System.Runtime.InteropServices;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Platform.Linux
{
    /// <summary>
    /// Global hotkeys on Wayland read from /dev/input, so they fire whatever window is focused. Needs the
    /// user in the 'input' group. Matches like XWaylandHotkeyPoller.
    /// </summary>
    internal sealed class EvdevHotkeyPoller : ILinuxHotkeySource
    {
        private const string InputDir = "/dev/input";
        private const int O_RDONLY = 0, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
        private const short POLLIN = 0x1;
        private const int EINTR = 4, EAGAIN = 11;

        private const ushort EV_SYN = 0, EV_KEY = 1;
        private const ushort SYN_DROPPED = 3;
        private const int KeyCount = 0x300; // KEY_CNT
        private const int KeyBytes = KeyCount / 8;
        private const int BtnMisc = 0x100; // mouse/gamepad buttons start here

        // struct input_event on 64-bit: timeval (16 bytes), u16 type, u16 code, s32 value
        private const int EventSize = 24;

        // _IOC(_IOC_READ, 'E', nr, len)
        private static nuint EvIoc(int nr, int len) => (nuint)((2u << 30) | ((uint)len << 16) | ('E' << 8) | (uint)nr);
        private static readonly nuint EVIOCGKEY = EvIoc(0x18, KeyBytes);
        private static readonly nuint EVIOCGBIT_KEY = EvIoc(0x20 + EV_KEY, KeyBytes);

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buf, nuint count);
        [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint request, byte[] arg);
        [DllImport("libc", SetLastError = true)] private static extern int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);

        private const string LibX11 = "libX11.so.6";
        private const int XKeycodeOffset = 8; // X keycodes are evdev codes + 8

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void IOErrorExitHandler(IntPtr display, IntPtr userData);

        [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
        [DllImport(LibX11)] private static extern int XDisplayKeycodes(IntPtr display, out int minKeycode, out int maxKeycode);
        [DllImport(LibX11)] private static extern IntPtr XGetKeyboardMapping(IntPtr display, byte firstKeycode, int keycodeCount, out int keysymsPerKeycode);
        [DllImport(LibX11)] private static extern int XFree(IntPtr data);
        [DllImport(LibX11)] private static extern void XSetIOErrorExitHandler(IntPtr display, IOErrorExitHandler handler, IntPtr userData);

        // By default a lost X connection makes libX11 exit() the process
        private static readonly IOErrorExitHandler OnIOErrorExit = (_, _) => { };

        private sealed class Device
        {
            public required string Path { get; init; }
            public required int Fd { get; init; }
            public bool[] Down { get; } = new bool[KeyCount];
        }

        private sealed class Binding
        {
            public required HotkeyAction Action { get; init; }
            public required int[][] Keys { get; init; } // per key, the evdev codes that count as it (left/right modifiers)
            public bool WasDown { get; set; }
        }

        private readonly object _lock = new();
        private readonly Action<HotkeyAction> _onFired;
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;
        private readonly List<Device> _devices = [];
        private readonly byte[] _buffer = new byte[EventSize * 64];
        private List<Hotkey>? _pendingHotkeys;
        private string _appliedSignature = "";
        private Binding[] _bindings = [];
        private DateTime _nextScan = DateTime.MinValue;
        private bool _disposed;

        private EvdevHotkeyPoller(List<Device> devices, Action<HotkeyAction> onFired)
        {
            _devices = devices;
            _onFired = onFired;
            _thread = new Thread(Run) { IsBackground = true, Name = "Segra.Hotkeys.Evdev" };
            _thread.Start();
        }

        /// <summary>Returns null when no keyboard can be opened; <paramref name="permissionDenied"/> says whether one exists but isn't readable.</summary>
        public static EvdevHotkeyPoller? TryStart(Action<HotkeyAction> onFired, out bool permissionDenied)
        {
            permissionDenied = false;
            if (!Directory.Exists(InputDir))
                return null;

            var devices = new List<Device>();
            foreach (var path in EventPaths())
            {
                var device = TryOpenKeyboard(path, out bool denied);
                if (device != null)
                    devices.Add(device);
                permissionDenied |= denied;
            }

            if (devices.Count == 0)
                return null;

            permissionDenied = false;
            Log.Information($"Reading hotkeys from {devices.Count} input device(s): {string.Join(", ", devices.Select(d => d.Path))}");
            return new EvdevHotkeyPoller(devices, onFired);
        }

        public void SetBindings(IEnumerable<Hotkey> hotkeys)
        {
            lock (_lock)
                _pendingHotkeys = hotkeys.ToList();
        }

        private static IEnumerable<string> EventPaths()
        {
            try
            {
                return Directory.GetFiles(InputDir, "event*");
            }
            catch (Exception ex)
            {
                Log.Debug($"Could not list {InputDir}: {ex.Message}");
                return [];
            }
        }

        private static Device? TryOpenKeyboard(string path, out bool permissionDenied)
        {
            permissionDenied = false;
            int fd = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
            if (fd < 0)
            {
                permissionDenied = Marshal.GetLastWin32Error() == 13; // EACCES
                return null;
            }

            var bits = new byte[KeyBytes];
            bool isKeyboard = ioctl(fd, EVIOCGBIT_KEY, bits) >= 0
                && Enumerable.Range(1, BtnMisc - 1).Any(code => (bits[code >> 3] & (1 << (code & 7))) != 0);
            if (!isKeyboard)
            {
                close(fd);
                return null;
            }

            var device = new Device { Path = path, Fd = fd };
            SyncKeyState(device);
            return device;
        }

        private static void SyncKeyState(Device device)
        {
            var bits = new byte[KeyBytes];
            if (ioctl(device.Fd, EVIOCGKEY, bits) < 0)
            {
                Array.Clear(device.Down);
                return;
            }
            for (int code = 0; code < KeyCount; code++)
                device.Down[code] = (bits[code >> 3] & (1 << (code & 7))) != 0;
        }

        private void Run()
        {
            while (!_stop.IsSet)
            {
                List<Hotkey>? pending;
                lock (_lock)
                {
                    pending = _pendingHotkeys;
                    _pendingHotkeys = null;
                }

                if (pending != null)
                    ApplyBindings(pending);

                if (DateTime.UtcNow >= _nextScan)
                {
                    ScanForNewDevices();
                    _nextScan = DateTime.UtcNow.AddSeconds(2);
                }

                var fds = _devices.Select(d => new PollFd { Fd = d.Fd, Events = POLLIN }).ToArray();
                // Wake regularly for binding changes, hotplug and Dispose
                int ready = poll(fds, (nuint)fds.Length, 100);
                if (ready < 0 && Marshal.GetLastWin32Error() != EINTR)
                {
                    Log.Warning($"Polling input devices failed (errno {Marshal.GetLastWin32Error()}); hotkeys stay off until Segra restarts");
                    return;
                }

                if (ready > 0)
                {
                    for (int i = fds.Length - 1; i >= 0; i--)
                    {
                        if (fds[i].Revents != 0 && !ReadEvents(_devices[i]))
                            RemoveDevice(_devices[i]);
                    }
                }

                Binding? fired = null;
                foreach (var binding in _bindings)
                {
                    bool down = AllDown(binding.Keys);
                    if (down && !binding.WasDown && (fired is null || binding.Keys.Length > fired.Keys.Length))
                        fired = binding;
                    binding.WasDown = down;
                }

                if (fired is not null)
                {
                    try { _onFired(fired.Action); }
                    catch (Exception ex) { Log.Error(ex, $"Hotkey action {fired.Action} failed"); }
                }
            }
        }

        // Returns false when the device is gone
        private bool ReadEvents(Device device)
        {
            while (true)
            {
                nint n = read(device.Fd, _buffer, (nuint)_buffer.Length);
                if (n < 0)
                {
                    int errno = Marshal.GetLastWin32Error();
                    return errno == EAGAIN || errno == EINTR;
                }
                if (n == 0)
                    return false;

                for (int offset = 0; offset + EventSize <= n; offset += EventSize)
                {
                    ushort type = BitConverter.ToUInt16(_buffer, offset + 16);
                    ushort code = BitConverter.ToUInt16(_buffer, offset + 18);
                    int value = BitConverter.ToInt32(_buffer, offset + 20);

                    if (type == EV_KEY && code < KeyCount)
                        device.Down[code] = value != 0; // 1 press, 2 autorepeat, 0 release
                    else if (type == EV_SYN && code == SYN_DROPPED)
                        SyncKeyState(device);
                }
            }
        }

        private void ScanForNewDevices()
        {
            foreach (var path in EventPaths())
            {
                if (_devices.Any(d => d.Path == path))
                    continue;
                var device = TryOpenKeyboard(path, out _);
                if (device != null)
                {
                    _devices.Add(device);
                    Log.Information($"Reading hotkeys from new input device {path}");
                }
            }
        }

        private void RemoveDevice(Device device)
        {
            close(device.Fd);
            _devices.Remove(device);
            Log.Information($"Input device {device.Path} was removed");
        }

        private void ApplyBindings(List<Hotkey> hotkeys)
        {
            // Settings saves resend unchanged hotkeys; re-seeding would swallow a press landing in the same tick
            string signature = string.Join(";", hotkeys.Select(h => $"{h.Action}:{string.Join("+", h.Keys)}"));
            if (signature == _appliedSignature)
                return;
            _appliedSignature = signature;

            var layout = ReadLayout();
            if (layout == null)
                Log.Information("Could not read the keyboard layout from XWayland; hotkeys use US key positions");
            var bindings = new List<Binding>();
            foreach (var hotkey in hotkeys)
            {
                int[][] keys = hotkey.Keys.Select(vk => ToKeyCodes(vk, layout)).ToArray();
                if (keys.Length == 0 || keys.Any(codes => codes.Length == 0))
                {
                    Log.Warning($"Hotkey {hotkey.Action} uses a key that can't be read from input devices (keys {string.Join("+", hotkey.Keys)}), so it won't fire");
                    continue;
                }
                // Seed from the current state so a key already held when the binding is applied does not fire
                bindings.Add(new Binding { Action = hotkey.Action, Keys = keys, WasDown = AllDown(keys) });
            }
            _bindings = [.. bindings];
        }

        private bool AllDown(int[][] keys) =>
            keys.All(codes => codes.Any(code => _devices.Any(d => d.Down[code])));

        // VK codes are layout-dependent but evdev codes are physical keys, so resolve them through the user's layout
        private static int[] ToKeyCodes(int vk, Dictionary<nuint, List<int>>? layout)
        {
            if (layout != null)
            {
                int[] codes = XWaylandHotkeyPoller.ToKeysyms(vk)
                    .SelectMany(keysym => layout.TryGetValue(keysym, out var keyCodes) ? keyCodes : [])
                    .Distinct()
                    .ToArray();
                if (codes.Length > 0)
                    return codes;
            }
            return ToUsKeyCodes(vk);
        }

        // Keysym -> evdev codes of the keys producing it, from XWayland's keymap. Null without an X server.
        private static Dictionary<nuint, List<int>>? ReadLayout()
        {
            IntPtr display = IntPtr.Zero;
            try
            {
                display = XOpenDisplay(IntPtr.Zero);
                if (display == IntPtr.Zero)
                    return null;
                XSetIOErrorExitHandler(display, OnIOErrorExit, IntPtr.Zero);

                XDisplayKeycodes(display, out int minKeycode, out int maxKeycode);
                int count = maxKeycode - minKeycode + 1;
                IntPtr mapping = XGetKeyboardMapping(display, (byte)minKeycode, count, out int perKeycode);
                if (mapping == IntPtr.Zero)
                    return null;

                var layout = new Dictionary<nuint, List<int>>();
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        int evdevCode = minKeycode + i - XKeycodeOffset;
                        if (evdevCode <= 0 || evdevCode >= KeyCount)
                            continue;
                        for (int level = 0; level < perKeycode; level++)
                        {
                            var keysym = (nuint)Marshal.ReadIntPtr(mapping, (i * perKeycode + level) * IntPtr.Size);
                            if (keysym == 0)
                                continue;
                            // The VK table uses uppercase letter keysyms
                            if (keysym >= 0x61 && keysym <= 0x7A)
                                keysym -= 0x20;
                            if (!layout.TryGetValue(keysym, out var keyCodes))
                                layout[keysym] = keyCodes = [];
                            if (!keyCodes.Contains(evdevCode))
                                keyCodes.Add(evdevCode);
                        }
                    }
                }
                finally
                {
                    XFree(mapping);
                }
                return layout;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // No libX11, or older than 1.7 (no exit handler)
                return null;
            }
            finally
            {
                if (display != IntPtr.Zero)
                    XCloseDisplay(display);
            }
        }

        // Fallback when the layout can't be read
        private static int[] ToUsKeyCodes(int vk) => vk switch
        {
            >= 0x41 and <= 0x5A => [LetterCodes[vk - 0x41]], // A-Z
            0x30 => [11], // 0
            >= 0x31 and <= 0x39 => [vk - 0x31 + 2], // 1-9
            >= 0x70 and <= 0x79 => [vk - 0x70 + 59], // F1-F10
            0x7A => [87], // F11
            0x7B => [88], // F12
            >= 0x7C and <= 0x87 => [vk - 0x7C + 183], // F13-F24
            0x60 => [82], // keypad 0
            >= 0x61 and <= 0x63 => [vk - 0x61 + 79], // keypad 1-3
            >= 0x64 and <= 0x66 => [vk - 0x64 + 75], // keypad 4-6
            >= 0x67 and <= 0x69 => [vk - 0x67 + 71], // keypad 7-9
            0x10 => [42, 54], // Shift
            0x11 => [29, 97], // Control
            0x12 => [56, 100], // Alt
            0x5B or 0x5C => [125, 126], // Super
            0x0D => [28, 96], // Enter and keypad Enter, as on Windows
            0x08 => [14], // Backspace
            0x09 => [15], // Tab
            0x13 => [119], // Pause
            0x14 => [58], // Caps Lock
            0x1B => [1], // Escape
            0x20 => [57], // Space
            0x21 => [104], // Page Up
            0x22 => [109], // Page Down
            0x23 => [107], // End
            0x24 => [102], // Home
            0x25 => [105], // Left
            0x26 => [103], // Up
            0x27 => [106], // Right
            0x28 => [108], // Down
            0x2C => [99], // Print Screen
            0x2D => [110], // Insert
            0x2E => [111], // Delete
            0x5D => [127], // Menu
            0x6A => [55], // keypad *
            0x6B => [78], // keypad +
            0x6D => [74], // keypad -
            0x6E => [83], // keypad decimal
            0x6F => [98], // keypad /
            0x90 => [69], // Num Lock
            0x91 => [70], // Scroll Lock
            0xBA => [39], // ;
            0xBB => [13], // =
            0xBC => [51], // ,
            0xBD => [12], // -
            0xBE => [52], // .
            0xBF => [53], // /
            0xC0 => [41], // `
            0xDB => [26], // [
            0xDC => [43], // backslash
            0xDD => [27], // ]
            0xDE => [40], // '
            0xE2 => [86], // the extra key next to left Shift on ISO keyboards (< > |)
            _ => []
        };

        private static readonly int[] LetterCodes =
        [
            30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50, // A-M
            49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44, // N-Z
        ];

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _stop.Set();
            // Only close the devices once the thread is done with them
            if (_thread.Join(TimeSpan.FromSeconds(1)))
            {
                foreach (var device in _devices)
                    close(device.Fd);
                _devices.Clear();
            }
        }
    }
}
