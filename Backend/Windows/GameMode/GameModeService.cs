using Serilog;
using Microsoft.Win32;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Windows.GameMode
{
    internal static class GameModeService
    {
        // Windows stores the "Game Mode" toggle (Settings > Gaming > Game Mode) here.
        // AutoGameModeEnabled is a DWORD: 1 = Game Mode on, 0 = Game Mode off.
        private const string GameBarKeyPath = @"Software\Microsoft\GameBar";
        private const string AutoGameModeValueName = "AutoGameModeEnabled";

        /// <summary>
        /// Ensures Windows Game Mode is turned off, but only when the user has opted in via the
        /// DisableWindowsGameMode setting. When the setting is false this is a no-op: Segra never
        /// turns Game Mode on, it only ensures it stays off when explicitly asked to.
        /// </summary>
        public static void EnforceDisabledIfEnabled()
        {
            if (!Settings.Instance.DisableWindowsGameMode)
            {
                return;
            }

            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(GameBarKeyPath, writable: true);
                if (key == null)
                {
                    Log.Warning("Could not open registry key {Key} to disable Windows Game Mode", GameBarKeyPath);
                    return;
                }

                // A missing value means Game Mode is on, since it defaults to enabled in Windows.
                bool isEnabled = key.GetValue(AutoGameModeValueName) is not int value || value != 0;
                if (!isEnabled)
                {
                    Log.Information("Windows Game Mode already disabled");
                    return;
                }

                key.SetValue(AutoGameModeValueName, 0, RegistryValueKind.DWord);
                Log.Information("Disabled Windows Game Mode (was enabled)");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to disable Windows Game Mode");
            }
        }
    }
}
