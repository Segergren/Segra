using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Segra.Backend.App;

namespace Segra.Backend.Platform.Windows
{
    /// <summary>
    /// Notification-area icon backed by Shell_NotifyIcon with a fixed GUID identity.
    ///
    /// The GUID is what Windows uses to key the per-app "Other system tray icons"
    /// show/hide setting (HKCU\Control Panel\NotifyIconSettings), so a stable GUID
    /// is what makes the user's choice survive restarts. WinForms' NotifyIcon
    /// registers without a GUID and derives its identity from runtime data, which
    /// explorer keys inconsistently across sessions - the toggle then resets on
    /// every reboot.
    /// </summary>
    internal sealed class WindowsTrayIcon : ITrayIcon
    {
        // Fixed forever: changing it orphans the persisted per-app tray setting.
        private static readonly Guid IconGuid = new("fe057fbf-5ab5-45e8-8800-01ebd100763b");

        private const uint NIF_MESSAGE = 0x1;
        private const uint NIF_ICON = 0x2;
        private const uint NIF_TIP = 0x4;
        private const uint NIF_GUID = 0x20;

        private const uint NIM_ADD = 0x0;
        private const uint NIM_MODIFY = 0x1;
        private const uint NIM_SETVERSION = 0x4;

        private const uint NOTIFYICON_VERSION_4 = 4;

        private const uint WM_TRAY_CALLBACK = 0x8001; // WM_APP + 1
        private const uint WM_SWAP_ICON = 0x8002;     // WM_APP + 2

        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_CONTEXTMENU = 0x007B;

        private IntPtr _hwnd;
        private WndProcDelegate? _wndProcDelegate;
        private Icon? _idleIcon;
        private Icon? _recordingIcon;
        private Action? _onOpen;
        private Action? _onExit;
        private ContextMenuStrip? _menu;
        private readonly Lock _stateLock = new();
        private bool _recording;
        private bool _registered;

        public void Initialize(Action onOpen, Action onExit)
        {
            _onOpen = onOpen;
            _onExit = onExit;

            var trayThread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                _idleIcon = Properties.Resources.icon;
                _recordingIcon = Properties.Resources.iconRecording;
                _hwnd = CreateSinkWindow();
                if (_hwnd == IntPtr.Zero)
                {
                    return;
                }

                _menu = new ContextMenuStrip();
                _menu.Items.Add("Open", null, (s, e) => onOpen());
                _menu.Items.Add("Exit", null, (s, e) => onExit());

                AddIcon(_idleIcon);
                if (_registered)
                {
                    NotifyIconService.Initialize(this);
                }

                // Standard message loop (message-only sink window + menu popups).
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            });
            trayThread.SetApartmentState(ApartmentState.STA);
            trayThread.IsBackground = true;
            trayThread.Start();
        }

        /// <summary>Swaps the tray icon between the idle and recording glyphs.</summary>
        public void SetRecording(bool recording)
        {
            lock (_stateLock)
            {
                _recording = recording;
                if (_hwnd != IntPtr.Zero)
                {
                    PostMessage(_hwnd, WM_SWAP_ICON, recording ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
                }
            }
        }

        private IntPtr CreateSinkWindow()
        {
            _wndProcDelegate = WndProc;
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = GetModuleHandle(null),
                lpszClassName = "SegraTrayIconSink"
            };
            RegisterClassW(ref wc);
            return CreateWindowExW(0, "SegraTrayIconSink", "SegraTrayIconSink", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        }

        private void AddIcon(Icon icon)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 0,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID,
                uCallbackMessage = WM_TRAY_CALLBACK,
                hIcon = icon.Handle,
                szTip = "Segra",
                guidItem = IconGuid
            };
            _registered = Shell_NotifyIconW(NIM_ADD, ref data);

            if (_registered)
            {
                var version = new NOTIFYICONDATA
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hwnd,
                    uID = 0,
                    uFlags = NIF_GUID,
                    uVersionOrTimeout = NOTIFYICON_VERSION_4,
                    guidItem = IconGuid
                };
                Shell_NotifyIconW(NIM_SETVERSION, ref version);
            }
        }

        private void SwapIcon(bool recording)
        {
            Icon icon = recording ? (_recordingIcon ?? _idleIcon!) : (_idleIcon ?? _recordingIcon!);
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 0,
                uFlags = NIF_GUID | NIF_ICON,
                hIcon = icon.Handle,
                guidItem = IconGuid
            };
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAY_CALLBACK)
            {
                switch (lParam.ToInt32())
                {
                    case (int)WM_LBUTTONDBLCLK:
                        _onOpen?.Invoke();
                        break;
                    case (int)WM_RBUTTONUP:
                    case (int)WM_CONTEXTMENU:
                        ShowMenu();
                        break;
                }
                return IntPtr.Zero;
            }

            if (msg == WM_SWAP_ICON)
            {
                lock (_stateLock)
                {
                    if (_registered)
                    {
                        SwapIcon(wParam != IntPtr.Zero);
                    }
                }
                return IntPtr.Zero;
            }

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private void ShowMenu()
        {
            if (_menu == null)
            {
                return;
            }

            // Required so the popup doesn't immediately dismiss itself.
            SetForegroundWindow(_hwnd);
            _menu.Show(Cursor.Position);
        }

        // --- P/Invoke ---

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersionOrTimeout;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassW(ref WNDCLASS wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);
    }
}
