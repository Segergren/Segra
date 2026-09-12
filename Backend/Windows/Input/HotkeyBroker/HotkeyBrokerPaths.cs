using System.Diagnostics;
using Segra.Hotkeys;

namespace Segra.Backend.Windows.Input.HotkeyBroker
{
    /// <summary>
    /// Locations and identifiers for the privileged hotkey broker. The broker is the only Segra
    /// binary that requests UIAccess, so it must be installed under Program Files; everything else
    /// stays in the normal per-user install.
    /// </summary>
    internal static class HotkeyBrokerPaths
    {
        public static string InstallDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Segra");

        /// <summary>The signed broker we expect to find (and launch) from Program Files.</summary>
        public static string InstalledBrokerPath =>
            Path.Combine(InstallDirectory, HotkeyProtocol.BrokerExeName);

        /// <summary>The broker shipped alongside the app, copied into Program Files on install.</summary>
        public static string BundledBrokerPath =>
            Path.Combine(AppContext.BaseDirectory, HotkeyProtocol.BrokerExeName);

        public static bool IsInstalled => File.Exists(InstalledBrokerPath);

        /// <summary>File version of the bundled broker, or null when this build ships none.</summary>
        public static string? BundledVersion => VersionOf(BundledBrokerPath);

        /// <summary>
        /// True when the installed broker has the same file version as the bundled one, or when
        /// nothing is bundled to compare against (development builds). The broker carries its own
        /// version, so a Segra release without broker changes never asks for an update.
        /// </summary>
        public static bool IsInstalledCurrent()
        {
            if (!IsInstalled)
                return false;

            string? bundled = BundledVersion;
            return bundled is null || VersionOf(InstalledBrokerPath) == bundled;
        }

        private static string? VersionOf(string path) =>
            File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;

        /// <summary>Per-session pipe so multiple signed-in users each get their own broker.</summary>
        public static string PipeName =>
            HotkeyProtocol.PipeNameForSession(Process.GetCurrentProcess().SessionId);
    }
}
