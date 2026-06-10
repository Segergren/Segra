using Segra.Backend.App;
using Segra.Backend.Services;
using Serilog;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Segra.Backend.Windows.Input
{
    /// <summary>
    /// Detects when a recorded game runs at a higher integrity level than Segra
    /// (e.g. GTA V Enhanced Online launched through BattlEye runs elevated).
    ///
    /// A low-level keyboard hook (WH_KEYBOARD_LL) installed by a medium-integrity
    /// process does not receive keystrokes while a higher-integrity window is
    /// focused, so global hotkeys (bookmark, save replay, etc.) silently stop
    /// working in those games. When that situation is detected we warn the user
    /// once that Segra has to be run as administrator for hotkeys to work there.
    /// </summary>
    internal static class HotkeyElevationService
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenIntegrityLevel = 25; // TOKEN_INFORMATION_CLASS.TokenIntegrityLevel

        // Tracks which game PIDs we've already warned about so the modal only shows once per session.
        private static readonly HashSet<int> _warnedPids = new();
        private static readonly object _lock = new();

        /// <summary>
        /// If the given game process runs at a higher integrity level than Segra and the user
        /// has hotkeys enabled, shows a one-time warning that Segra must run as administrator
        /// for hotkeys to work in that game. Safe to call on a background thread.
        /// </summary>
        public static async Task WarnIfHotkeysBlockedAsync(int gamePid, string gameName)
        {
            try
            {
                // Only relevant if the user actually has hotkeys configured.
                bool hasEnabledKeybindings = Settings.Instance.Keybindings?.Any(k => k.Enabled) == true;
                if (!hasEnabledKeybindings)
                    return;

                lock (_lock)
                {
                    if (_warnedPids.Contains(gamePid))
                        return;
                }

                int? selfIntegrity = GetProcessIntegrityLevel(Process.GetCurrentProcess().Id);
                int? gameIntegrity = GetProcessIntegrityLevel(gamePid);

                // If we can't determine either integrity level, don't risk a false warning.
                if (!selfIntegrity.HasValue || !gameIntegrity.HasValue)
                    return;

                // The hook is only blocked when the game is strictly higher integrity than us.
                if (gameIntegrity.Value <= selfIntegrity.Value)
                    return;

                lock (_lock)
                {
                    if (!_warnedPids.Add(gamePid))
                        return;
                }

                Log.Information($"Hotkeys are blocked for {gameName} (PID {gamePid}): game integrity 0x{gameIntegrity.Value:X} > Segra integrity 0x{selfIntegrity.Value:X}. Segra must run as administrator.");

                await MessageService.ShowModal(
                    title: "Hotkeys won't work in this game",
                    description: $"{gameName} is running with administrator privileges, so Segra can't capture its global hotkeys " +
                                 "(such as creating a bookmark or saving the replay buffer) while the game is focused.\n\n" +
                                 "To use hotkeys in this game, close Segra and run it as administrator.",
                    type: "warning"
                );
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to check hotkey elevation for PID {gamePid}: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the integrity level RID of the given process (e.g. 0x2000 = medium, 0x3000 = high/elevated),
        /// or null if it could not be determined.
        /// </summary>
        private static int? GetProcessIntegrityLevel(int pid)
        {
            IntPtr hProcess = IntPtr.Zero;
            IntPtr hToken = IntPtr.Zero;
            IntPtr pTokenInfo = IntPtr.Zero;
            try
            {
                hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProcess == IntPtr.Zero)
                    return null;

                if (!OpenProcessToken(hProcess, TOKEN_QUERY, out hToken))
                    return null;

                // First call to get the required buffer size.
                GetTokenInformation(hToken, TokenIntegrityLevel, IntPtr.Zero, 0, out int size);
                if (size <= 0)
                    return null;

                pTokenInfo = Marshal.AllocHGlobal(size);
                if (!GetTokenInformation(hToken, TokenIntegrityLevel, pTokenInfo, size, out size))
                    return null;

                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label { IntPtr Sid; uint Attributes; } }
                IntPtr pSid = Marshal.ReadIntPtr(pTokenInfo);
                if (pSid == IntPtr.Zero)
                    return null;

                // The integrity level RID is the last sub-authority of the SID.
                IntPtr pSubAuthorityCount = GetSidSubAuthorityCount(pSid);
                byte subAuthorityCount = Marshal.ReadByte(pSubAuthorityCount);
                if (subAuthorityCount == 0)
                    return null;

                IntPtr pRid = GetSidSubAuthority(pSid, (uint)(subAuthorityCount - 1));
                return Marshal.ReadInt32(pRid);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (pTokenInfo != IntPtr.Zero) Marshal.FreeHGlobal(pTokenInfo);
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);
    }
}
