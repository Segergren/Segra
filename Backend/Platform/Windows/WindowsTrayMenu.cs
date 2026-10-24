using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Segra.Backend.Platform.Windows
{
    // Native popup menu for the tray icon. Windows draws it in the system style (rounded corners on
    // Windows 11, dark in dark mode), which the WinForms ContextMenuStrip does not.
    internal static class WindowsTrayMenu
    {
        public const int Open = 1;
        public const int ResetWindowSize = 2;
        public const int Quit = 3;
        public const int StopRecording = 4;
        public const int StartRecording = 5;
        public const int SaveReplay = 6;
        public const int ToggleAutoRecordGames = 7;

        // Tailwind red-500, the app's recording dot
        private static readonly Color RecordingRed = Color.FromArgb(251, 44, 54);

        // Undocumented uxtheme exports that let native menus follow the Windows dark mode setting.
        public static void EnableDarkMode()
        {
            try
            {
                SetPreferredAppMode(PreferredAppModeAllowDark);
                FlushMenuThemes();
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        // Shows the menu at the cursor and returns the chosen command, or 0 if it was dismissed.
        public static int Show(IntPtr owner, Icon icon, TrayActions actions)
        {
            bool recording = actions.IsRecording();
            bool canStartRecording = !recording && actions.CanStartRecording();
            bool canSaveReplay = actions.CanSaveReplay();
            bool canResetWindowSize = actions.CanResetWindowSize();
            GetCursorPos(out POINT cursor);
            int iconSize = GetSmallIconSize(cursor);
            // Lucide glyphs take the menu text color, which follows the Windows app theme
            Color glyphColor = IsDarkMode() ? Color.White : Color.Black;
            IntPtr menu = CreatePopupMenu();
            var bitmaps = new List<IntPtr>();
            try
            {
                var menuInfo = new MENUINFO { cbSize = (uint)Marshal.SizeOf<MENUINFO>(), fMask = MIM_STYLE, dwStyle = MNS_CHECKORBMP };
                SetMenuInfo(menu, ref menuInfo);

                // Opens Segra or brings it to the front; bold as the default (double-click) action
                AppendMenu(menu, MF_STRING, Open, "Open Segra");
                SetMenuDefaultItem(menu, Open, 0);
                SetItemBitmap(menu, Open, CreateIconBitmap(icon, iconSize), bitmaps);
                if (recording || canStartRecording || canSaveReplay)
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                if (recording)
                {
                    AppendMenu(menu, MF_STRING, StopRecording, "Stop recording");
                    SetItemBitmap(menu, StopRecording, CreateDotBitmap(RecordingRed, iconSize), bitmaps);
                }
                else if (canStartRecording)
                {
                    AppendMenu(menu, MF_STRING, StartRecording, "Start display recording");
                    SetItemBitmap(menu, StartRecording, CreateLucideBitmap(iconSize, glyphColor, DrawMonitor), bitmaps);
                }
                if (canSaveReplay)
                {
                    AppendMenu(menu, MF_STRING, SaveReplay, "Save replay");
                    SetItemBitmap(menu, SaveReplay, CreateLucideBitmap(iconSize, glyphColor, DrawHistory), bitmaps);
                }
                AppendMenu(menu, MF_SEPARATOR, 0, null);
                // A native checkmark in the icon column
                AppendMenu(menu, MF_STRING | (actions.IsAutoRecordGamesEnabled() ? MF_CHECKED : 0), ToggleAutoRecordGames, "Record games automatically");
                if (canResetWindowSize)
                {
                    AppendMenu(menu, MF_STRING, ResetWindowSize, "Reset window size");
                    SetItemBitmap(menu, ResetWindowSize, CreateLucideBitmap(iconSize, glyphColor, DrawMaximize), bitmaps);
                }
                AppendMenu(menu, MF_SEPARATOR, 0, null);
                AppendMenu(menu, MF_STRING, Quit, "Quit Segra");
                SetItemBitmap(menu, Quit, CreateLucideBitmap(iconSize, glyphColor, DrawX), bitmaps);

                // Open upward from a bottom taskbar and downward from a top one
                Rectangle screen = Screen.FromPoint(new Point(cursor.X, cursor.Y)).Bounds;
                uint align = cursor.Y > screen.Top + screen.Height / 2 ? TPM_BOTTOMALIGN : TPM_TOPALIGN;

                // A tray menu only closes on an outside click when its owner is the foreground window
                SetForegroundWindow(owner);
                int command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | align, cursor.X, cursor.Y, owner, IntPtr.Zero);
                PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                return command;
            }
            finally
            {
                DestroyMenu(menu);
                foreach (IntPtr bitmap in bitmaps)
                    DeleteObject(bitmap);
            }
        }

        // The menu doesn't own item bitmaps, so they are collected for deletion after it closes.
        private static void SetItemBitmap(IntPtr menu, int command, IntPtr bitmap, List<IntPtr> bitmaps)
        {
            if (bitmap == IntPtr.Zero)
                return;

            bitmaps.Add(bitmap);
            var item = new MENUITEMINFO { cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_BITMAP, hbmpItem = bitmap };
            SetMenuItemInfo(menu, (uint)command, false, ref item);
        }

        private static bool IsDarkMode() =>
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

        private static int GetSmallIconSize(POINT point)
        {
            IntPtr monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
            uint dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX : 96;
            return GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        }

        private static IntPtr CreateIconBitmap(Icon icon, int size)
        {
            using var sized = new Icon(icon, size, size);
            using var source = sized.ToBitmap();
            return CreateMenuBitmap(size, graphics =>
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, size, size);
            });
        }

        private static IntPtr CreateDotBitmap(Color color, int size)
        {
            return CreateSupersampledBitmap(size, graphics =>
            {
                float diameter = size * 0.625f * Supersample; // 10 px at 100% scaling
                float offset = (size * Supersample - diameter) / 2;
                using var brush = new SolidBrush(color);
                graphics.FillEllipse(brush, offset, offset, diameter, diameter);
            });
        }

        // Lucide icons (24-unit grid, 2-unit round strokes) with whole-pixel strokes.
        private static IntPtr CreateLucideBitmap(int size, Color color, Action<LucideGlyph> draw)
        {
            float scale = size / 24f;
            float stroke = MathF.Max(1, MathF.Round(2 * scale));
            return CreateSupersampledBitmap(size, graphics =>
            {
                using var pen = new Pen(color, stroke * Supersample) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                draw(new LucideGlyph(graphics, pen, scale, (int)stroke % 2 == 1, Supersample));
            });
        }

        private const int Supersample = 4;

        // Drawn at 4x on the pixel grid and averaged down: GDI+ antialiasing alone renders diagonals too light and shapes off-center.
        private static IntPtr CreateSupersampledBitmap(int size, Action<Graphics> draw)
        {
            int largeSize = size * Supersample;
            using var bitmap = new Bitmap(largeSize, largeSize, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                draw(graphics);
            }

            byte[] large = ReadPixels(bitmap);
            var pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    for (int channel = 0; channel < 4; channel++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < Supersample; dy++)
                            for (int dx = 0; dx < Supersample; dx++)
                                sum += large[((y * Supersample + dy) * largeSize + x * Supersample + dx) * 4 + channel];
                        pixels[(y * size + x) * 4 + channel] = (byte)(sum / (Supersample * Supersample));
                    }
                }
            }

            return CreateDib(size, pixels);
        }

        // lucide "monitor", the Display Capture button icon
        private static void DrawMonitor(LucideGlyph glyph)
        {
            glyph.RoundedRect(2, 3, 20, 14, 2);
            glyph.Line(8, 21, 16, 21);
            glyph.Line(12, 17, 12, 21);
        }

        // lucide "history", the Replay Buffer sidebar icon
        private static void DrawHistory(LucideGlyph glyph)
        {
            glyph.Arc(12, 12, 9, 180, -270);
            glyph.Arc(12.0367f, 12.7499f, 9.75f, -90.216f, -43.815f);
            glyph.LineToGrid(5.26f, 5.74f, 3, 8);
            glyph.Polyline(3, 3, 3, 8, 8, 8);
            glyph.Polyline(12, 7, 12, 12, 16, 14);
        }

        // lucide "maximize-2"
        private static void DrawMaximize(LucideGlyph glyph)
        {
            glyph.Polyline(15, 3, 21, 3, 21, 9);
            glyph.Line(21, 3, 14, 10);
            glyph.Line(3, 21, 10, 14);
            glyph.Polyline(9, 21, 3, 21, 3, 15);
        }

        // lucide "x", the app's close icon, enlarged from 6-18 to sit with the other icons
        private static void DrawX(LucideGlyph glyph)
        {
            glyph.Line(19.5f, 4.5f, 4.5f, 19.5f);
            glyph.Line(4.5f, 4.5f, 19.5f, 19.5f);
        }

        // Draws on the lucide grid. Straight strokes snap to whole pixels instead of blurring across two; diagonals keep their angle.
        private sealed class LucideGlyph(Graphics graphics, Pen pen, float scale, bool oddStroke, int supersample)
        {
            private float Offset => oddStroke ? 0.5f : 0f;

            private float Grid(float units) => (MathF.Floor(units * scale - Offset + 0.5f) + Offset) * supersample;

            private float Exact(float units) => (units * scale + Offset) * supersample;

            public void Line(float x1, float y1, float x2, float y2)
            {
                Func<float, float> map = x1 == x2 || y1 == y2 ? Grid : Exact;
                graphics.DrawLine(pen, map(x1), map(y1), map(x2), map(y2));
            }

            // From an arc end to a snapped polyline corner, so the two meet without a smudge
            public void LineToGrid(float x1, float y1, float x2, float y2) =>
                graphics.DrawLine(pen, Exact(x1), Exact(y1), Grid(x2), Grid(y2));

            // Straight segments joined with a round corner
            public void Polyline(params float[] coordinates)
            {
                var points = new PointF[coordinates.Length / 2];
                for (int i = 0; i < points.Length; i++)
                    points[i] = new PointF(Grid(coordinates[i * 2]), Grid(coordinates[i * 2 + 1]));
                graphics.DrawLines(pen, points);
            }

            // Angles in degrees, clockwise from the positive x axis
            public void Arc(float cx, float cy, float radius, float startAngle, float sweepAngle)
            {
                float r = radius * scale * supersample;
                graphics.DrawArc(pen, Exact(cx) - r, Exact(cy) - r, r * 2, r * 2, startAngle, sweepAngle);
            }

            public void RoundedRect(float x, float y, float width, float height, float radius)
            {
                float left = Grid(x), top = Grid(y), right = Grid(x + width), bottom = Grid(y + height);
                float diameter = radius * 2 * scale * supersample;
                using var path = new GraphicsPath();
                path.AddArc(left, top, diameter, diameter, 180, 90);
                path.AddArc(right - diameter, top, diameter, diameter, 270, 90);
                path.AddArc(right - diameter, bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(left, bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                graphics.DrawPath(pen, path);
            }
        }

        private static IntPtr CreateMenuBitmap(int size, Action<Graphics> draw)
        {
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                draw(graphics);
            }

            return CreateDib(size, ReadPixels(bitmap));
        }

        private static byte[] ReadPixels(Bitmap bitmap)
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var pixels = new byte[bitmap.Width * bitmap.Height * 4];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        // Menu item bitmaps only keep their transparency as 32bpp premultiplied-alpha DIBs.
        private static IntPtr CreateDib(int size, byte[] pixels)
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down rows, matching LockBits
                biPlanes = 1,
                biBitCount = 32,
            };
            IntPtr dib = CreateDIBSection(IntPtr.Zero, ref header, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib != IntPtr.Zero)
                Marshal.Copy(pixels, 0, bits, pixels.Length);
            return dib;
        }

        private const int PreferredAppModeAllowDark = 1;
        private const uint MIM_STYLE = 0x00000010;
        private const uint MNS_CHECKORBMP = 0x04000000;
        private const uint MIIM_BITMAP = 0x00000080;
        private const uint MF_STRING = 0x00000000;
        private const uint MF_CHECKED = 0x00000008;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_TOPALIGN = 0x0000;
        private const uint TPM_BOTTOMALIGN = 0x0020;
        private const uint TPM_NONOTIFY = 0x0080;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint WM_NULL = 0x0000;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;
        private const int SM_CXSMICON = 49;
        private const uint DIB_RGB_COLORS = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MENUINFO
        {
            public uint cbSize;
            public uint fMask;
            public uint dwStyle;
            public uint cyMax;
            public IntPtr hbrBack;
            public uint dwContextHelpID;
            public nuint dwMenuData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MENUITEMINFO
        {
            public uint cbSize;
            public uint fMask;
            public uint fType;
            public uint fState;
            public uint wID;
            public IntPtr hSubMenu;
            public IntPtr hbmpChecked;
            public IntPtr hbmpUnchecked;
            public nuint dwItemData;
            public IntPtr dwTypeData;
            public uint cch;
            public IntPtr hbmpItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        private static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        private static extern void FlushMenuThemes();

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll", EntryPoint = "SetMenuItemInfoW")]
        private static extern bool SetMenuItemInfo(IntPtr hMenu, uint item, bool fByPosition, ref MENUITEMINFO lpmii);

        [DllImport("user32.dll")]
        private static extern bool SetMenuInfo(IntPtr hMenu, ref MENUINFO lpcmi);

        [DllImport("user32.dll")]
        private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr ho);
    }
}
