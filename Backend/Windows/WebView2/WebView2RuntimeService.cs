using Microsoft.Win32;
using Serilog;

namespace Segra.Backend.Windows.WebView2
{
    /// <summary>
    /// Logs the installed Microsoft Edge WebView2 runtime version at startup. Photino renders
    /// through WebView2 on Windows; installation is handled by Velopack at install time
    /// (vpk pack --framework webview2), so this only reports what is present.
    /// </summary>
    public static class WebView2RuntimeService
    {
        // Evergreen runtime registration written by the WebView2 installer, per Microsoft's
        // detection guidance. The Edge updater is 32-bit, so per-machine installs land under
        // WOW6432Node on x64 Windows. "pv" of 0.0.0.0 means the runtime was removed.
        private const string ClientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        public static void LogRuntimeVersion()
        {
            string? version = GetInstalledVersion();
            if (version != null)
            {
                Log.Information("WebView2 runtime version {Version}", version);
            }
            else
            {
                Log.Warning("WebView2 runtime is not installed");
            }
        }

        private static string? GetInstalledVersion()
        {
            string? version =
                ReadVersion(Registry.LocalMachine, $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{ClientGuid}") ??
                ReadVersion(Registry.LocalMachine, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{ClientGuid}") ??
                ReadVersion(Registry.CurrentUser, $@"Software\Microsoft\EdgeUpdate\Clients\{ClientGuid}");

            return string.IsNullOrEmpty(version) || version == "0.0.0.0" ? null : version;
        }

        private static string? ReadVersion(RegistryKey root, string subKeyPath)
        {
            using var key = root.OpenSubKey(subKeyPath);
            return key?.GetValue("pv") as string;
        }
    }
}
