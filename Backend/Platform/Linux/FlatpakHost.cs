using Serilog;
using System.Diagnostics;

namespace Segra.Backend.Platform.Linux
{
    // Reads host process state via flatpak-spawn, since our /proc only lists our own PID namespace.
    internal static class FlatpakHost
    {
        public static bool IsFlatpak { get; } =
            File.Exists("/.flatpak-info")
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID"));

        /// <summary>The app id we run as, used to build the host `flatpak run` command line.</summary>
        public static string AppId { get; } =
            Environment.GetEnvironmentVariable("FLATPAK_ID") ?? "tv.segra.Segra";

        // Pin to the user's home: flatpak-spawn runs in the caller's cwd, and /app/segra doesn't exist on the host.
        public static string DirectoryArg { get; } = "--directory=" +
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? home : "/");

        // One `find` call emits every host pid + exe as "/proc/<pid> <path>" lines, instead of a readlink-per-pid shell loop.
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
                // Exe paths can contain spaces, so split "/proc/1234 /usr/bin/foo" on the first separator only.
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

                // Keep the previous list rather than reporting every game as exited, but log if it never recovers.
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

        // Every value of an env var across all host processes (one spawn, not one per pid), or null on failure.
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

                // Read both pipes concurrently so neither fills and blocks the child; kill on timeout.
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
                // find exits non-zero when a /proc entry vanishes mid-walk, so only an empty result is a failure.
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
