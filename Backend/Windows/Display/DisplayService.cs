using Serilog;
using System.Management;
using Segra.Backend.Core.Models;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Segra.Backend.Windows.Display
{
    public static class DisplayService
    {
        private static List<Core.Models.Display> pendingDisplays = new();

        // QueryDisplayConfig flags/constants, mirroring HdrDetectionService.
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
        private const int ERROR_SUCCESS = 0;

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public int outputTechnology;
            public int rotation;
            public int scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public int scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // Only the source-mode fields (needed to detect the primary display) are exposed;
        // the rest of the union is left as padding, matching the real 64-byte native struct.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)] public uint infoType;
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
            [FieldOffset(16)] public uint sourceWidth;
            [FieldOffset(20)] public uint sourceHeight;
            [FieldOffset(24)] public uint sourcePixelFormat;
            [FieldOffset(28)] public int sourcePositionX;
            [FieldOffset(32)] public int sourcePositionY;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public int outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfoEx
        {
            public int Size;
            public RECT Monitor;
            public RECT WorkArea;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        public static bool GetPrimaryMonitorPhysicalResolution(out uint width, out uint height)
        {
            width = 0;
            height = 0;

            try
            {
                var primaryScreen = Screen.PrimaryScreen;
                if (primaryScreen == null) return false;

                DEVMODE devMode = new DEVMODE();
                devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

                if (EnumDisplaySettings(primaryScreen.DeviceName, ENUM_CURRENT_SETTINGS, ref devMode))
                {
                    width = (uint)devMode.dmPelsWidth;
                    height = (uint)devMode.dmPelsHeight;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to get physical resolution: {ex.Message}");
            }

            return false;
        }

        public static bool LoadAvailableMonitorsIntoState()
        {
            pendingDisplays.Clear();
            EnumerateMonitorsViaDisplayConfig();

            var newMaxHeight = GetMaxDisplayHeight();
            var currentDisplays = AppState.Instance.Displays;
            var currentMaxHeight = AppState.Instance.MaxDisplayHeight;

            bool displaysChanged = currentDisplays == null || !currentDisplays.SequenceEqual(pendingDisplays);
            bool maxHeightChanged = currentMaxHeight != newMaxHeight;

            if (!displaysChanged && !maxHeightChanged)
            {
                return false;
            }

            if (displaysChanged)
            {
                Log.Information("=== Available Monitors ===");
                foreach (var display in pendingDisplays)
                {
                    Log.Information("Monitor: {FriendlyName}, DeviceId: {DeviceID}, Primary: {IsPrimary}, HDR: {IsHdr}",
                        display.DeviceName, display.DeviceId, display.IsPrimary, display.IsHdr);
                }
                Log.Information("=== End Monitor List ===");

                AppState.Instance.Displays = new List<Core.Models.Display>(pendingDisplays);
            }

            if (maxHeightChanged)
            {
                Log.Information("Max display height changed: {MaxHeight}p", newMaxHeight);
                AppState.Instance.MaxDisplayHeight = newMaxHeight;
            }

            return true;
        }

        /// <summary>
        /// Checks if any connected display has a height of at least the specified value
        /// </summary>
        public static bool HasDisplayWithMinHeight(int minHeight)
        {
            return GetMaxDisplayHeight() >= minHeight;
        }

        /// <summary>
        /// Gets the maximum height among all connected displays
        /// </summary>
        public static int GetMaxDisplayHeight()
        {
            int maxHeight = 1080; // Default fallback

            try
            {
                foreach (var screen in Screen.AllScreens)
                {
                    DEVMODE devMode = new DEVMODE();
                    devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

                    if (EnumDisplaySettings(screen.DeviceName, ENUM_CURRENT_SETTINGS, ref devMode))
                    {
                        if (devMode.dmPelsHeight > maxHeight)
                        {
                            maxHeight = devMode.dmPelsHeight;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to get max display height: {Message}", ex.Message);
            }

            return maxHeight;
        }

        /// <summary>
        /// Resolves the monitor a window is on to that monitor's device interface path
        /// (the same id stored on Display.DeviceId), or null if it cannot be determined.
        /// </summary>
        public static string? GetDeviceIdForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return null;

            try
            {
                IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (hMonitor == IntPtr.Zero)
                    return null;

                MonitorInfoEx mi = new MonitorInfoEx();
                mi.Size = Marshal.SizeOf(mi);
                if (!GetMonitorInfo(hMonitor, ref mi))
                    return null;

                DisplayDevice device = new DisplayDevice();
                device.Size = Marshal.SizeOf(device);
                if (!EnumDisplayDevices(mi.DeviceName, 0, ref device, 1))
                    return null;

                return device.DeviceID;
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to resolve device id for window: {Message}", ex.Message);
                return null;
            }
        }

        private static void EnumerateMonitorsViaDisplayConfig()
        {
            try
            {
                int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (err != ERROR_SUCCESS || pathCount == 0)
                    return;

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

                err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (err != ERROR_SUCCESS)
                    return;

                for (int i = 0; i < pathCount; i++)
                {
                    var path = paths[i];
                    var target = path.targetInfo;

                    var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    name.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                    name.header.adapterId = target.adapterId;
                    name.header.id = target.id;

                    if (DisplayConfigGetDeviceInfo(ref name) != ERROR_SUCCESS)
                        continue;

                    string deviceId = name.monitorDevicePath;
                    string friendlyName = GetFriendlyMonitorName(deviceId, name.monitorFriendlyDeviceName);

                    var display = new Core.Models.Display
                    {
                        DeviceName = friendlyName,
                        DeviceId = deviceId,
                        IsPrimary = IsSourcePrimary(path.sourceInfo, modes),
                        IsHdr = HdrDetectionService.IsDisplayHdrActive(deviceId)
                    };

                    pendingDisplays.Add(display);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to enumerate monitors via DisplayConfig: {Message}", ex.Message);
            }
        }

        private static bool IsSourcePrimary(DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo, DISPLAYCONFIG_MODE_INFO[] modes)
        {
            if (sourceInfo.modeInfoIdx >= modes.Length)
                return false;

            var mode = modes[sourceInfo.modeInfoIdx];
            if (mode.infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                return false;

            return mode.sourcePositionX == 0 && mode.sourcePositionY == 0;
        }

        private static string GetFriendlyMonitorName(string deviceId, string fallback)
        {
            // deviceId looks like:  \\?\DISPLAY#SAM6507#5&23dce28b&0&UID265988_0#
            // The middle segment is the PnP ID we need (SAM6507 in this case).
            var match = Regex.Match(deviceId, @"#(?<pnpid>[A-Z0-9]{7})#",
                                    RegexOptions.IgnoreCase);
            if (!match.Success) return fallback;

            string pnpId = match.Groups["pnpid"].Value;

            // Ask WMI for a matching PnP entity and read its Name.
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name,PNPDeviceID FROM Win32_PnPEntity " +
                $"WHERE PNPDeviceID LIKE '%{pnpId}%'");

            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["Name"] is string name && !string.IsNullOrWhiteSpace(name))
                {
                    // Extract model name from inside parentheses if present
                    // e.g. "Generic Monitor (Odyssey G60SD)" -> "Odyssey G60SD"
                    var modelMatch = Regex.Match(name, @"\(([^\)]+)\)");
                    if (modelMatch.Success)
                    {
                        return modelMatch.Groups[1].Value.Trim();
                    }
                    return name; // Return full name if no parentheses found
                }
            }

            return fallback; // give up – use whatever the driver said
        }
    }
}
