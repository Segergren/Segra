using System.ComponentModel;
using Serilog;
using Segra.Backend.App;
using Segra.Backend.Core;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Windows.Input.HotkeyBroker
{
    /// <summary>
    /// Keeps the broker installed without a dedicated UI. When hotkeys start and the bundled broker
    /// is missing from Program Files or newer than the installed one, the elevated install runs on
    /// its own (one UAC prompt). A decline or failure is remembered per bundled version so the
    /// prompt never repeats for the same broker; Settings then shows a warning card whose button
    /// runs the same install by hand.
    /// </summary>
    internal static class HotkeyBrokerSetup
    {
        private static int _autoAttempted;
        private static int _busy;

        /// <summary>Publishes the current state to the frontend and starts the automatic install when due.</summary>
        public static void Refresh(bool connected, bool rejectedProtocol)
        {
            bool installed = HotkeyBrokerPaths.IsInstalled;
            bool upToDate = installed && !rejectedProtocol && HotkeyBrokerPaths.IsInstalledCurrent();
            string? bundled = HotkeyBrokerPaths.BundledVersion;
            bool needsInstall = bundled is not null && !upToDate;
            bool declined = needsInstall && Settings.Instance.HotkeyBrokerDeclinedVersion == bundled;

            AppState.Instance.HotkeyBroker = new HotkeyBrokerStatus(installed, upToDate, connected, declined);

            if (needsInstall && !declined && Interlocked.Exchange(ref _autoAttempted, 1) == 0)
                _ = InstallAsync();
        }

        /// <summary>Runs the elevated install (one UAC prompt) and records the outcome.</summary>
        public static async Task InstallAsync()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1)
                return;

            try
            {
                int exitCode = HotkeyBrokerInstaller.RequestInstall();
                if (exitCode == HotkeyBrokerInstaller.ExitOk)
                {
                    Log.Information("Hotkey broker installed to {Path}", HotkeyBrokerPaths.InstalledBrokerPath);
                    SetDeclinedVersion(null);
                }
                else
                {
                    Log.Error("Hotkey broker install failed (exit code {ExitCode})", exitCode);
                    SetDeclinedVersion(HotkeyBrokerPaths.BundledVersion);
                    await MessageService.ShowModal("Hotkey helper failed", HotkeyBrokerInstaller.DescribeExitCode(exitCode), "error");
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Log.Information("Hotkey broker install declined at the UAC prompt");
                SetDeclinedVersion(HotkeyBrokerPaths.BundledVersion);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Hotkey broker install failed");
                SetDeclinedVersion(HotkeyBrokerPaths.BundledVersion);
                await MessageService.ShowModal("Hotkey helper failed", ex.Message, "error");
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
                KeybindCaptureService.PublishBrokerStatus();
            }
        }

        private static void SetDeclinedVersion(string? version)
        {
            if (Settings.Instance.HotkeyBrokerDeclinedVersion == version)
                return;

            Settings.Instance.HotkeyBrokerDeclinedVersion = version;
            SettingsService.SaveSettings();
        }
    }
}
