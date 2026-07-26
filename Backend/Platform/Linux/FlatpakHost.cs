using Serilog;
using System.Diagnostics;

namespace Segra.Backend.Platform.Linux
{
    /// <summary>
    /// Reads host process state from inside a Flatpak sandbox. Flatpak gives the app its own PID
    /// namespace, so /proc lists only Segra's own processes while game detection needs the host's.
    /// flatpak-spawn runs the read outside the sandbox; it needs --talk-name=org.freedesktop.Flatpak
    /// in the manifest. Outside Flatpak none of this is used and /proc is read directly.
    /// </summary>
    internal static class FlatpakHost
    {
        public static bool IsFlatpak { get; } =
            File.Exists("/.flatpak-info")
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID"));

        // Every host pid with its resolved exe, as "/proc/<pid> <path>" lines. A shell loop calling
        // readlink per pid would fork a few hundred times per poll, several times a second, while a
        // game is running; find emits the same data from one process.
        private static readonly string[] ListProcesses =
            ["find", "/proc", "-maxdepth", "2", "-name", "exe", "-type", "l", "-printf", "%h %l\n"];

        private static readonly object _lock = new();
        private static Dictionary<int, string> _processes = [];
        private static DateTime _processesUtc = DateTime.MinValue;

        /// <summary>Re-reads the host process list. Called once per poll cycle.</summary>
        public static Dictionary<int, string> RefreshProcesses()
        {
            var map = new Dictionary<int, string>();
            foreach (var line in RunOnHost(ListProcesses).Split('\n'))
            {
                // "/proc/1234 /usr/bin/foo". Exe paths can contain spaces, so split on the first
                // separator only. /proc/self and /proc/thread-self fail the pid parse and drop out.
                const int prefix = 6; // "/proc/"
                int sp = line.IndexOf(' ');
                if (sp <= prefix || !line.StartsWith("/proc/", StringComparison.Ordinal)) continue;
                if (!int.TryParse(line.AsSpan(prefix, sp - prefix), out int pid)) continue;
                map[pid] = line[(sp + 1)..].Trim();
            }

            lock (_lock)
            {
                // An empty result means the spawn failed; keep the previous list and retry next call
                // rather than reporting that every game exited.
                if (map.Count > 0)
                {
                    _processes = map;
                    _processesUtc = DateTime.UtcNow;
                }
                return _processes;
            }
        }

        /// <summary>Exe path for a host pid, refreshing the list if it is older than <paramref name="maxAge"/>.</summary>
        public static string ExePath(int pid, TimeSpan maxAge)
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _processesUtc <= maxAge && _processes.TryGetValue(pid, out string? cached))
                    return cached;
            }
            return RefreshProcesses().TryGetValue(pid, out string? fresh) ? fresh : string.Empty;
        }

        /// <summary>Contents of a host file (used for /proc/&lt;pid&gt;/environ), or empty if unreadable.</summary>
        public static string ReadFile(string path) => RunOnHost("cat", path);

        private static string RunOnHost(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("flatpak-spawn")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                // ArgumentList passes each argument verbatim, with no quoting round-trip.
                psi.ArgumentList.Add("--host");
                foreach (string a in args) psi.ArgumentList.Add(a);

                using var proc = Process.Start(psi);
                if (proc == null) return string.Empty;

                // Read both pipes concurrently so neither fills and blocks the child. Kill on timeout:
                // this runs on the detection poll timer, and a blocking read would stall detection for
                // good rather than just losing one cycle.
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(10000))
                {
                    Log.Warning("flatpak-spawn --host timed out; killing it");
                    try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    proc.WaitForExit();
                }
                errTask.GetAwaiter().GetResult();
                return outTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning($"flatpak-spawn --host failed: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
