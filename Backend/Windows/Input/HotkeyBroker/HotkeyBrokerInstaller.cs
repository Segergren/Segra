using System.Diagnostics;
using Segra.Hotkeys;

namespace Segra.Backend.Windows.Input.HotkeyBroker
{
    /// <summary>
    /// Installs the signed UIAccess broker into Program Files, the only location Windows grants
    /// UIAccess from. The copy runs in an elevated instance of Segra started with
    /// <see cref="InstallArgument"/>, so the user sees one UAC prompt; the broker itself never
    /// prompts. The elevated instance cannot open the main log (the running instance holds it),
    /// so it reports through its exit code.
    /// </summary>
    internal static class HotkeyBrokerInstaller
    {
        public const string InstallArgument = "--install-hotkey-broker";

        public const int ExitOk = 0;
        public const int ExitBundledMissing = 1;
        public const int ExitSignatureRejected = 2;
        public const int ExitFileOperationFailed = 3;

        /// <summary>
        /// Relaunches Segra elevated to perform the install and returns its exit code. Throws a
        /// <see cref="System.ComponentModel.Win32Exception"/> with code 1223 if the user declines UAC.
        /// </summary>
        public static int RequestInstall()
        {
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not resolve the Segra executable path.");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = InstallArgument,
                UseShellExecute = true,
                Verb = "runas",
            }) ?? throw new InvalidOperationException("The elevated helper did not start.");

            process.WaitForExit();
            return process.ExitCode;
        }

        public static string DescribeExitCode(int exitCode) => exitCode switch
        {
            ExitBundledMissing => "The helper is missing from this Segra install. Reinstalling Segra restores it.",
            ExitSignatureRejected => "The helper failed the signature check, so it was not installed.",
            ExitFileOperationFailed => "Windows refused to change the helper in Program Files. Close Segra.Hotkeys.exe and try again.",
            _ => $"The helper exited with code {exitCode}.",
        };

        /// <summary>Runs in the elevated instance: verifies and copies the bundled broker into Program Files.</summary>
        public static int RunInstallElevated()
        {
            string source = HotkeyBrokerPaths.BundledBrokerPath;
            if (!File.Exists(source))
                return ExitBundledMissing;

            // The source sits in the user-writable install, so only accept a copy signed like Segra itself.
            string self = Environment.ProcessPath!;
            if (Authenticode.GetSignerThumbprint(self) is not null && !Authenticode.IsSignedLike(source, self))
                return ExitSignatureRejected;

            try
            {
                Directory.CreateDirectory(HotkeyBrokerPaths.InstallDirectory);
                StopRunningBroker();
                File.Copy(source, HotkeyBrokerPaths.InstalledBrokerPath, overwrite: true);
                return ExitOk;
            }
            catch (Exception)
            {
                return ExitFileOperationFailed;
            }
        }

        // A running broker locks its own image, so stop it before replacing the file.
        private static void StopRunningBroker()
        {
            foreach (var process in Process.GetProcessesByName("Segra.Hotkeys"))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path is null || !string.Equals(path, HotkeyBrokerPaths.InstalledBrokerPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch
                {
                    // Already gone, or not accessible; the copy surfaces any real failure.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }
}
