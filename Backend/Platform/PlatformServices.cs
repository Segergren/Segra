using System.Runtime.InteropServices;

namespace Segra.Backend.Platform
{
    /// <summary>
    /// Service locator for platform-specific implementations. <see cref="Initialize"/> is called
    /// once at startup and picks the Windows or Linux implementations (selected at compile time via
    /// the WINDOWS symbol, since the Windows/Linux implementation types are compiled per-TFM).
    /// </summary>
    internal static class PlatformServices
    {
        public static bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        public static ITrayIcon Tray { get; private set; } = null!;
        public static IAudioDeviceService Audio { get; private set; } = null!;
        public static IDisplayService Display { get; private set; } = null!;
        public static INativeDialogs Dialogs { get; private set; } = null!;
        public static IStartupManager Startup { get; private set; } = null!;
        public static ISoundPlayer Sound { get; private set; } = null!;

        public static void Initialize()
        {
#if WINDOWS
            Tray = new Windows.WindowsTrayIcon();
            Audio = new Windows.WindowsAudioDeviceService();
            Display = new Windows.WindowsDisplayService();
            Dialogs = new Windows.WindowsNativeDialogs();
            Startup = new Windows.WindowsStartupManager();
            Sound = new Windows.WindowsSoundPlayer();
#else
            Tray = new Linux.LinuxTrayIcon();
            Audio = new Linux.LinuxAudioDeviceService();
            Display = new Linux.LinuxDisplayService();
            Dialogs = new Linux.LinuxNativeDialogs();
            Startup = new Linux.LinuxStartupManager();
            Sound = new Linux.LinuxSoundPlayer();
#endif
        }
    }
}
