using Serilog;
using System.Runtime.InteropServices;

namespace Segra.Backend.Windows.Display
{
    /// <summary>
    /// Detects whether a Windows display is currently in HDR mode, using the Win32 DisplayConfig API.
    /// This is the same signal Windows exposes to apps: a display reports HDR only when the user has
    /// turned the "Use HDR" toggle on. Recording then mirrors what OBS captures (OBS produces HDR
    /// content automatically when the monitor is in HDR mode), so no user configuration is required.
    /// </summary>
    public static class HdrDetectionService
    {
        // QueryDisplayConfig flags
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

        // DisplayConfigGetDeviceInfo request types
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;   // pre-24H2 HDR boolean
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 13; // 24H2+, distinguishes HDR vs WCG

        // DISPLAYCONFIG_ADVANCED_COLOR_MODE (used by the _2 query)
        private const int DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR = 2;

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

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);

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

        // We never read mode info; the array just has to be the right element size (64 bytes) so
        // QueryDisplayConfig can fill it. The union payload is left as padding via the Size field.
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            // Bitfield: bit0 advancedColorSupported, bit1 advancedColorEnabled,
            // bit2 wideColorEnforced, bit3 advancedColorForceDisabled.
            public uint value;
            public int colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            // Bitfield: bit0 advancedColorSupported, bit1 advancedColorActive, bit2 reserved,
            // bit3 advancedColorLimitedByPolicy, bit4 highDynamicRangeSupported,
            // bit5 highDynamicRangeUserEnabled, bit6 wideColorSupported, bit7 wideColorUserEnabled.
            public uint value;
            public int colorEncoding;
            public uint bitsPerColorChannel;
            public int activeColorMode; // DISPLAYCONFIG_ADVANCED_COLOR_MODE
        }

        /// <summary>
        /// Returns true if the display identified by <paramref name="deviceId"/> (the monitor device
        /// interface path, e.g. <c>\\?\DISPLAY#...</c>, as stored on <see cref="Core.Models.Display.DeviceId"/>)
        /// is currently in HDR mode. Returns false on any error or if the display cannot be found.
        /// </summary>
        public static bool IsDisplayHdrActive(string? deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
                return false;

            try
            {
                int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (err != ERROR_SUCCESS || pathCount == 0)
                    return false;

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

                err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (err != ERROR_SUCCESS)
                    return false;

                for (int i = 0; i < pathCount; i++)
                {
                    var target = paths[i].targetInfo;

                    // Resolve the monitor device path for this target so we can match it to the
                    // caller's display. Matching by device path avoids any monitor-index ambiguity.
                    var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    name.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                    name.header.adapterId = target.adapterId;
                    name.header.id = target.id;

                    if (DisplayConfigGetDeviceInfo(ref name) != ERROR_SUCCESS)
                        continue;

                    if (!string.Equals(name.monitorDevicePath, deviceId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return QueryHdrActive(target.adapterId, target.id, name.monitorFriendlyDeviceName);
                }

                Log.Information("HDR detection: no active display path matched device id {DeviceId}", deviceId);
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("HDR detection failed for {DeviceId}: {Message}", deviceId, ex.Message);
                return false;
            }
        }

        private static bool QueryHdrActive(LUID adapterId, uint targetId, string friendlyName)
        {
            // Prefer the newer query (Windows 11 24H2+) which cleanly distinguishes HDR from
            // wide-color-gamut. Fall back to the original query on older Windows versions.
            var info2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
            info2.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
            info2.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>();
            info2.header.adapterId = adapterId;
            info2.header.id = targetId;

            if (DisplayConfigGetDeviceInfo(ref info2) == ERROR_SUCCESS)
            {
                bool hdrUserEnabled = (info2.value & 0x20u) != 0; // bit5 highDynamicRangeUserEnabled
                bool hdr = info2.activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR || hdrUserEnabled;
                Log.Information("HDR detection ({Monitor}): activeColorMode={Mode}, hdrUserEnabled={Enabled} -> HDR={Hdr}",
                    friendlyName, info2.activeColorMode, hdrUserEnabled, hdr);
                return hdr;
            }

            var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            info.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
            info.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
            info.header.adapterId = adapterId;
            info.header.id = targetId;

            if (DisplayConfigGetDeviceInfo(ref info) == ERROR_SUCCESS)
            {
                bool hdr = (info.value & 0x2u) != 0; // bit1 advancedColorEnabled
                Log.Information("HDR detection ({Monitor}): advancedColorEnabled -> HDR={Hdr}", friendlyName, hdr);
                return hdr;
            }

            Log.Information("HDR detection ({Monitor}): advanced color info unavailable, assuming SDR", friendlyName);
            return false;
        }
    }
}
