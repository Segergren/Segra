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

        /// <summary>The app id we run as, used to build the host `flatpak run` command line.</summary>
        public static string AppId { get; } =
            Environment.GetEnvironmentVariable("FLATPAK_ID") ?? "tv.segra.Segra";

        /// <summary>
        /// flatpak-spawn runs the host command in the CALLER's working directory, and ours (/app/segra)
        /// does not exist on the host, which makes the portal refuse to start anything at all. Pin every
        /// host command to the user's home, which is valid on both sides of the sandbox.
        /// </summary>
        public static string DirectoryArg { get; } = "--directory=" +
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? home : "/");

        // Every host pid with its resolved exe, as "/proc/<pid> <path>" lines. A shell loop calling
        // readlink per pid would fork a few hundred times per poll, several times a second, while a
        // game is running; find emits the same data from one process.
        private static readonly string[] ListProcesses =
            ["find", "/proc", "-maxdepth", "2", "-name", "exe", "-type", "l", "-printf", "%h %l\n"];

        private static readonly object _lock = new();
        private static Dictionary<int, string> _processes = [];
        private static DateTime _processesUtc = DateTime.MinValue;
        private static int _listFailures;

        /// <summary>Re-reads the host process list. Called once per poll cycle.</summary>
        public static Dictionary<int, string> RefreshProcesses()
        {
            var map = new Dictionary<int, string>();
            foreach (var line in (RunOnHost(ListProcesses) ?? string.Empty).Split('\n'))
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
                if (map.Count > 0)
                {
                    _processes = map;
                    _processesUtc = DateTime.UtcNow;
                    _listFailures = 0;
                    return _processes;
                }

                // Keep the previous list rather than reporting that every game exited, but a failure
                // that never recovers freezes detection completely, so make it visible.
                if (++_listFailures == 1 || _listFailures % 40 == 0)
                {
                    Log.Error($"Cannot read the host process list through flatpak-spawn ({_listFailures} attempts in a row). " +
                              "Game detection is stalled; the sandbox needs --talk-name=org.freedesktop.Flatpak.");
                }
                return _processes;
            }
        }

        /// <summary>True while a host pid is still alive, served from the poll snapshot.</summary>
        public static bool IsRunning(int pid, TimeSpan maxAge) => ExePath(pid, maxAge).Length > 0;

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
        public static string ReadFile(string path) => RunOnHost("cat", path) ?? string.Empty;

        /// <summary>
        /// Every value of an env var across all host processes, or null if the host call failed.
        /// One spawn for the whole process list: reading environ per pid would fork hundreds of times
        /// per poll while a game is recording.
        /// </summary>
        public static HashSet<string>? ReadEnvVarValues(string key)
        {
            // `|| true` keeps grep's "no match" exit code from looking like a failed spawn.
            string? output = RunOnHost("sh", "-c",
                $"grep -aoh -m1 '{key}=[^[:cntrl:]]*' /proc/[0-9]*/environ 2>/dev/null || true");
            if (output == null) return null;

            string prefix = key + "=";
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in output.Split('\n'))
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    values.Add(line[prefix.Length..].Trim());
            return values;
        }

        /// <summary>Runs a command on the host. Returns null when the spawn itself failed.</summary>
        private static string? RunOnHost(params string[] args)
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
                psi.ArgumentList.Add(DirectoryArg);
                foreach (string a in args) psi.ArgumentList.Add(a);

                using var proc = Process.Start(psi);
                if (proc == null) return null;

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
                    return null;
                }

                string output = outTask.GetAwaiter().GetResult();
                string error = errTask.GetAwaiter().GetResult();
                // find exits non-zero when a /proc entry vanishes mid-walk, so only an empty result
                // counts as a failure. Callers decide how loud that is.
                if (proc.ExitCode != 0 && output.Length == 0)
                {
                    Log.Debug($"flatpak-spawn --host {args[0]} exited {proc.ExitCode}: {error.Trim()}");
                    return null;
                }
                return output;
            }
            catch (Exception ex)
            {
                Log.Debug($"flatpak-spawn --host {args[0]} failed: {ex.Message}");
                return null;
            }
        }
    }
}
