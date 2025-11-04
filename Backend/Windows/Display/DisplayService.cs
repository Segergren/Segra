using Serilog;
using System.Runtime.InteropServices;
using Segra.Backend.Core.Models;
using System.Management;
using System.Text.RegularExpressions;

namespace Segra.Backend.Windows.Display
{
    public static class DisplayService
    {
        private static List<Core.Models.Display> displays = new();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MonitorInfoEx
        {
            public int Size;
            public RECT Monitor;
            public RECT WorkArea;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct DisplayDevice
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

        public static void LoadAvailableMonitorsIntoState()
        {
            displays.Clear();
            Log.Information("=== Available Monitors ===");
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumProc, IntPtr.Zero);
            Settings.Instance.State.Displays = displays;
            Log.Information("=== End Monitor List ===");
        }

        private static bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
        {
            MonitorInfoEx mi = new MonitorInfoEx();
            mi.Size = Marshal.SizeOf(mi);

            if (GetMonitorInfo(hMonitor, ref mi))
            {
                DisplayDevice device = new DisplayDevice();
                device.Size = Marshal.SizeOf(device);

                if (EnumDisplayDevices(mi.DeviceName, 0, ref device, 1))
                {
                    string friendlyName = GetFriendlyMonitorName(device.DeviceID, device.DeviceString);
                    Log.Information("Monitor Found:");
                    Log.Information("  Device Name: {DeviceName}", mi.DeviceName);
                    Log.Information("  Friendly Name: {FriendlyName}", friendlyName);
                    Log.Information("  Device ID: {DeviceID}", device.DeviceID);
                    Log.Information("  Display String: {DeviceString}", device.DeviceString);
                    Log.Information("  Resolution: {Width}x{Height}",
                        mi.Monitor.Right - mi.Monitor.Left,
                        mi.Monitor.Bottom - mi.Monitor.Top);
                    Log.Information("  Position: {X},{Y}", mi.Monitor.Left, mi.Monitor.Top);
                    Log.Information("  Primary: {IsPrimary}", (mi.Flags & 1) != 0);
                    Log.Information("---");
                    displays.Add(new Core.Models.Display { DeviceName = friendlyName, DeviceId = device.DeviceID, IsPrimary = (mi.Flags & 1) != 0 });
                }
            }

            return true;
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
