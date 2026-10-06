using Serilog;
using System.Runtime.InteropServices;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Platform.Linux
{
    /// <summary>
    /// Global hotkeys on Wayland, where libobs never sees keys pressed in other apps. Wine/Proton games
    /// are X11 windows, so their keys reach XWayland, whose key state any X client can poll. Matches like
    /// the Windows hotkey broker: a binding fires once when all its keys are down, unrelated held keys
    /// don't block it, and when bindings overlap the one with the most keys wins.
    /// </summary>
    internal sealed class XWaylandHotkeyPoller : ILinuxHotkeySource
    {
        private const string LibX11 = "libX11.so.6";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int IOErrorHandler(IntPtr display);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void IOErrorExitHandler(IntPtr display, IntPtr userData);

        [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
        [DllImport(LibX11)] private static extern int XQueryKeymap(IntPtr display, byte[] keys);
        [DllImport(LibX11)] private static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);
        [DllImport(LibX11)] private static extern IntPtr XSetIOErrorHandler(IOErrorHandler handler);
        [DllImport(LibX11)] private static extern void XSetIOErrorExitHandler(IntPtr display, IOErrorExitHandler handler, IntPtr userData);

        // By default a lost X connection makes libX11 exit() the process; these let the poller stop instead.
        // Held in static fields so the GC can't collect them while libX11 holds the pointers.
        private static volatile bool _connectionLost;
        private static readonly IOErrorHandler OnIOError = _ => { _connectionLost = true; return 0; };
        private static readonly IOErrorExitHandler OnIOErrorExit = (_, _) => { };

        private sealed class Binding
        {
            public required HotkeyAction Action { get; init; }
            public required byte[][] Keys { get; init; } // per key, the keycodes that count as it (left/right modifiers)
            public bool WasDown { get; set; }
        }

        private readonly object _lock = new();
        private readonly IntPtr _display;
        private readonly Action<HotkeyAction> _onFired;
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;
        private readonly byte[] _keymap = new byte[32];
        // Only the poll thread talks to X: after an I/O error libX11 leaves the display locked, so any other
        // thread touching it would hang. Other threads just hand over new bindings here.
        private List<Hotkey>? _pendingHotkeys;
        private string _appliedSignature = "";
        private Binding[] _bindings = [];
        private bool _disposed;

        private XWaylandHotkeyPoller(IntPtr display, Action<HotkeyAction> onFired)
        {
            _display = display;
            _onFired = onFired;
            _thread = new Thread(Run) { IsBackground = true, Name = "Segra.Hotkeys.XWayland" };
            _thread.Start();
        }

        /// <summary>Starts polling, or returns null when no X server is reachable (no XWayland, or a Flatpak without X11 access).</summary>
        public static XWaylandHotkeyPoller? TryStart(Action<HotkeyAction> onFired)
        {
            IntPtr display = IntPtr.Zero;
            try
            {
                display = XOpenDisplay(IntPtr.Zero);
                if (display == IntPtr.Zero)
                    return null;

                _connectionLost = false;
                XSetIOErrorExitHandler(display, OnIOErrorExit, IntPtr.Zero);
                XSetIOErrorHandler(OnIOError);
                return new XWaylandHotkeyPoller(display, onFired);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // libX11 older than 1.7 has no exit handler, so a lost connection would end the process
                if (display != IntPtr.Zero)
                    XCloseDisplay(display);
                return null;
            }
        }

        public void SetBindings(IEnumerable<Hotkey> hotkeys)
        {
            lock (_lock)
                _pendingHotkeys = hotkeys.ToList();
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

                Binding? fired = null;
                if (_bindings.Length > 0 && !_connectionLost)
                {
                    XQueryKeymap(_display, _keymap);
                    foreach (var binding in _bindings)
                    {
                        bool down = AllDown(binding.Keys);
                        if (down && !binding.WasDown && (fired is null || binding.Keys.Length > fired.Keys.Length))
                            fired = binding;
                        binding.WasDown = down;
                    }
                }

                // XQueryKeymap reports success even when the connection broke (with garbage keys), so check the flag
                if (_connectionLost)
                {
                    Log.Warning("Lost the connection to XWayland; hotkeys stay off until Segra restarts");
                    return;
                }

                if (fired is not null)
                {
                    try { _onFired(fired.Action); }
                    catch (Exception ex) { Log.Error(ex, $"Hotkey action {fired.Action} failed"); }
                }

                _stop.Wait(25);
            }
        }

        private void ApplyBindings(List<Hotkey> hotkeys)
        {
            // Settings saves resend unchanged hotkeys; re-seeding would swallow a press landing in the same tick
            string signature = string.Join(";", hotkeys.Select(h => $"{h.Action}:{string.Join("+", h.Keys)}"));
            if (signature == _appliedSignature)
                return;
            _appliedSignature = signature;

            XQueryKeymap(_display, _keymap);
            var bindings = new List<Binding>();
            foreach (var hotkey in hotkeys)
            {
                byte[][] keys = hotkey.Keys.Select(ToKeycodes).ToArray();
                if (keys.Length == 0 || keys.Any(codes => codes.Length == 0))
                {
                    Log.Warning($"Hotkey {hotkey.Action} uses a key XWayland can't report (keys {string.Join("+", hotkey.Keys)}), so it won't fire");
                    continue;
                }
                // Seed from the current state so a key already held when the binding is applied does not fire
                bindings.Add(new Binding { Action = hotkey.Action, Keys = keys, WasDown = AllDown(keys) });
            }
            _bindings = [.. bindings];
        }

        private bool AllDown(byte[][] keys) =>
            keys.All(codes => codes.Any(code => (_keymap[code >> 3] & (1 << (code & 7))) != 0));

        private byte[] ToKeycodes(int vk) =>
            ToKeysyms(vk).Select(keysym => XKeysymToKeycode(_display, keysym)).Where(code => code != 0).ToArray();

        // The frontend records Win32 VK codes; these are the X keysyms for the keys OBSKit's FromWindowsVirtualKey supports
        internal static nuint[] ToKeysyms(int vk) => vk switch
        {
            (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) or 0x20 => [(nuint)vk], // 0-9, A-Z and space share their ASCII code
            >= 0x70 and <= 0x87 => [(nuint)(0xFFBE + vk - 0x70)], // F1-F24
            >= 0x60 and <= 0x69 => [(nuint)(0xFFB0 + vk - 0x60)], // keypad 0-9
            0x10 => [0xFFE1, 0xFFE2], // Shift
            0x11 => [0xFFE3, 0xFFE4], // Control
            0x12 => [0xFFE9, 0xFFEA], // Alt
            0x5B or 0x5C => [0xFFEB, 0xFFEC], // Super
            0x0D => [0xFF0D, 0xFF8D], // Enter and keypad Enter, as on Windows
            0x6E => [0xFF9F, 0xFFAE], // keypad decimal (KP_Delete while NumLock is off)
            0x08 => [0xFF08],
            0x09 => [0xFF09],
            0x13 => [0xFF13],
            0x14 => [0xFFE5],
            0x1B => [0xFF1B],
            0x5D => [0xFF67],
            0x21 => [0xFF55],
            0x22 => [0xFF56],
            0x23 => [0xFF57],
            0x24 => [0xFF50],
            0x25 => [0xFF51],
            0x26 => [0xFF52],
            0x27 => [0xFF53],
            0x28 => [0xFF54],
            0x2C => [0xFF61],
            0x2D => [0xFF63],
            0x2E => [0xFFFF],
            0x6A => [0xFFAA],
            0x6B => [0xFFAB],
            0x6D => [0xFFAD],
            0x6F => [0xFFAF],
            0x90 => [0xFF7F],
            0x91 => [0xFF14],
            0xBA => [0x3B],
            0xBB => [0x3D],
            0xBC => [0x2C],
            0xBD => [0x2D],
            0xBE => [0x2E],
            0xBF => [0x2F],
            0xC0 => [0x60],
            0xDB => [0x5B],
            0xDC => [0x5C],
            0xDD => [0x5D],
            0xDE => [0x27],
            _ => []
        };

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _stop.Set();
            // A frozen X server can block the poll thread; it is a background thread, so don't wait on it for long.
            // After an I/O error libX11 leaves the display locked, so closing it would hang; leak it instead.
            if (_thread.Join(TimeSpan.FromSeconds(1)) && !_connectionLost)
                XCloseDisplay(_display);
        }
    }
}
