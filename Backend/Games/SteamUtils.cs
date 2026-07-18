using Serilog;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace Segra.Backend.Games
{
    /// <summary>
    /// Resolves the local machine's currently logged-in Steam persona (display) name,
    /// used by integrations that need to tell the local player apart from others in
    /// on-screen text (e.g. OCR'd kill feeds that reference players by their Steam name).
    /// </summary>
    internal static class SteamUtils
    {
        private static string? _cachedPersonaName;
        private static bool _resolved;

        public static string? GetCurrentUserPersonaName()
        {
            if (_resolved)
                return _cachedPersonaName;

            _resolved = true;
            try
            {
                var steamPath = GetSteamInstallPath();
                if (steamPath == null)
                {
                    Log.Warning("[Steam] Could not locate Steam install path in registry");
                    return null;
                }

                var loginUsersPath = Path.Combine(steamPath, "config", "loginusers.vdf");
                if (!File.Exists(loginUsersPath))
                {
                    Log.Warning($"[Steam] loginusers.vdf not found at {loginUsersPath}");
                    return null;
                }

                _cachedPersonaName = ParseMostRecentPersonaName(File.ReadAllText(loginUsersPath));
                if (_cachedPersonaName == null)
                    Log.Warning("[Steam] Could not resolve a persona name from loginusers.vdf");
            }
            catch (Exception ex)
            {
                Log.Warning($"[Steam] Failed to resolve current user persona name: {ex.Message}");
            }

            return _cachedPersonaName;
        }

        private static string? GetSteamInstallPath()
        {
            try
            {
                using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (hkcu?.GetValue("SteamPath") is string userPath && !string.IsNullOrWhiteSpace(userPath))
                    return userPath.Replace("/", "\\");

                using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                                 ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                if (hklm?.GetValue("InstallPath") is string installPath && !string.IsNullOrWhiteSpace(installPath))
                    return installPath.Replace("/", "\\");
            }
            catch (Exception ex)
            {
                Log.Warning($"[Steam] Failed to read Steam install path from registry: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// loginusers.vdf lists every account that has logged into Steam on this machine.
        /// The account marked "MostRecent" "1" is the one currently signed in; if none is
        /// flagged (e.g. mid-login) fall back to the one with the highest Timestamp.
        /// </summary>
        private static string? ParseMostRecentPersonaName(string vdfText)
        {
            var accountBlocks = Regex.Matches(vdfText, "\"\\d{17}\"\\s*\\{([^{}]*)\\}", RegexOptions.Singleline);

            string? fallbackName = null;
            long fallbackTimestamp = -1;

            foreach (Match block in accountBlocks)
            {
                var body = block.Groups[1].Value;
                var personaName = Regex.Match(body, "\"PersonaName\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                if (string.IsNullOrEmpty(personaName))
                    continue;

                var mostRecent = Regex.Match(body, "\"MostRecent\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                if (mostRecent == "1")
                    return personaName;

                var timestampText = Regex.Match(body, "\"Timestamp\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                if (long.TryParse(timestampText, out var timestamp) && timestamp > fallbackTimestamp)
                {
                    fallbackTimestamp = timestamp;
                    fallbackName = personaName;
                }
            }

            return fallbackName;
        }
    }
}
