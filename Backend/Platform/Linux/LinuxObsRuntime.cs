using Serilog;
using System.Runtime.InteropServices;

namespace Segra.Backend.Platform.Linux
{
    /// <summary>
    /// Resolves the OBS runtime on Linux (a bundled copy shipped with the app, or a system obs-studio
    /// install) and makes libobs loadable before anything touches it. Because the dynamic loader reads
    /// LD_LIBRARY_PATH only at process start, the process re-execs itself once with the correct
    /// environment. This lets the app work whether launched via run.sh, an AppImage AppRun, or directly.
    /// </summary>
    internal static class LinuxObsRuntime
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int execve(string path, string[] argv, string[] envp);

        /// <summary>
        /// Configures the OBS runtime environment and re-execs the process if needed. Returns normally
        /// (without re-exec) when already configured, or when no OBS runtime can be found (the app then
        /// starts and surfaces the "install obs-studio" guidance from the recorder path).
        /// </summary>
        public static void ConfigureAndReexecIfNeeded()
        {
            try
            {
                // Already configured by a launcher (run.sh) or by our own prior re-exec.
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SEGRA_OBS_DATA_PATH"))
                    || Environment.GetEnvironmentVariable("SEGRA_REEXEC") == "1")
                    return;

                var r = Resolve();
                if (r == null)
                {
                    Log.Warning("No OBS runtime found (no downloaded bundle, no bundled libobs, no system obs-studio).");
                    return;
                }

                string appDir = AppContext.BaseDirectory.TrimEnd('/');
                string existingLd = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                string newLd = $"{r.LibDir}:{appDir}" + (existingLd.Length > 0 ? ":" + existingLd : "");

                Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", newLd);
                Environment.SetEnvironmentVariable("SEGRA_OBS_MODULE_PATH", r.ModulePath);
                Environment.SetEnvironmentVariable("SEGRA_OBS_MODULE_DATA_PATH", r.ModuleDataPath);
                Environment.SetEnvironmentVariable("SEGRA_OBS_DATA_PATH", r.DataPath);
                Environment.SetEnvironmentVariable("SEGRA_REEXEC", "1");

                // Bundled ffmpeg -> on PATH so FFmpegService (thumbnails/waveforms/clips) uses it
                // instead of a system install.
                if (r.FfmpegDir != null)
                {
                    string existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                    Environment.SetEnvironmentVariable("PATH", $"{r.FfmpegDir}:{existingPath}");
                }

                // Bundled GStreamer H.264 codec -> so the WebKitGTK <video> element can play recordings
                // without the user installing gstreamer1.0-libav.
                if (r.GstPluginDir != null)
                {
                    Environment.SetEnvironmentVariable("GST_PLUGIN_SYSTEM_PATH_1_0", r.GstPluginDir);
                    Environment.SetEnvironmentVariable("GST_PLUGIN_PATH_1_0", r.GstPluginDir);
                }

                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    Log.Warning("Could not determine executable path; skipping re-exec (LD_LIBRARY_PATH may not apply to libobs).");
                    return;
                }

                var cmdArgs = Environment.GetCommandLineArgs();
                var argv = new string[cmdArgs.Length + 1];
                argv[0] = exePath;
                for (int i = 1; i < cmdArgs.Length; i++) argv[i] = cmdArgs[i];
                argv[^1] = null!; // null-terminate for execve

                var envList = new List<string>();
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                    envList.Add($"{e.Key}={e.Value}");
                envList.Add(null!); // null-terminate

                Log.Information($"Re-exec to apply OBS runtime (lib='{r.LibDir}', data='{r.DataPath}', ffmpeg={r.FfmpegDir != null}, gst={r.GstPluginDir != null}).");
                execve(exePath, argv, envList.ToArray());

                // If execve returns, it failed.
                Log.Error($"execve failed (errno {Marshal.GetLastWin32Error()}); continuing without re-exec.");
            }
            catch (Exception ex)
            {
                Log.Warning($"OBS runtime configuration failed: {ex.Message}");
            }
        }

        internal sealed class RuntimePaths
        {
            public required string LibDir { get; init; }
            public required string ModulePath { get; init; }
            public required string ModuleDataPath { get; init; }
            public required string DataPath { get; init; }
            public string? FfmpegDir { get; init; }
            public string? GstPluginDir { get; init; }
        }

        // Directory where a downloaded Linux OBS/recorder bundle is extracted.
        internal static string DownloadedBundleDir() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra", "obs");

        // Resolves the OBS runtime, or null if none is available.
        private static RuntimePaths? Resolve()
        {
            string appDir = AppContext.BaseDirectory.TrimEnd('/');

            // 1) A bundle downloaded from the API, or shipped self-contained with the app. Both use the
            //    lib/ + obs-plugins/ + data/ layout, and may also carry ffmpeg (bin/) and the gstreamer
            //    H.264 codec (lib/gstreamer-1.0/) so users need nothing pre-installed.
            foreach (var root in new[] { DownloadedBundleDir(), appDir })
            {
                if (!File.Exists(Path.Combine(root, "lib", "libobs.so.0"))) continue;
                return new RuntimePaths
                {
                    LibDir = Path.Combine(root, "lib"),
                    ModulePath = Path.Combine(root, "obs-plugins"),
                    ModuleDataPath = Path.Combine(root, "data", "obs-plugins", "%module%"),
                    DataPath = Path.Combine(root, "data", "libobs"),
                    FfmpegDir = FindFfmpegDir(root),
                    GstPluginDir = FindGstDir(root),
                };
            }

            // 2) System obs-studio install.
            string? sysLib = FindFirstDir(new[]
            {
                "/usr/lib/x86_64-linux-gnu", "/usr/lib64", "/usr/lib",
                "/usr/local/lib", "/usr/local/lib/x86_64-linux-gnu"
            }, d => File.Exists(Path.Combine(d, "libobs.so.0")));

            string? obsData = FindFirstDir(new[] { "/usr/share/obs", "/usr/local/share/obs" },
                d => Directory.Exists(Path.Combine(d, "libobs")));

            if (sysLib == null || obsData == null)
                return null;

            string runtime = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra", "obs-runtime");
            string rtLib = Path.Combine(runtime, "lib");
            string rtPlugins = Path.Combine(runtime, "obs-plugins");

            try
            {
                if (Directory.Exists(runtime)) Directory.Delete(runtime, true);
                Directory.CreateDirectory(rtLib);
                Directory.CreateDirectory(rtPlugins);

                // Core libobs libraries + unversioned aliases (the loader and OBS graphics module need them).
                foreach (var so in Directory.GetFiles(sysLib, "libobs*.so*"))
                {
                    string b = Path.GetFileName(so);
                    CreateSymlink(so, Path.Combine(rtLib, b));
                    string un = System.Text.RegularExpressions.Regex.Replace(b, @"\.so\.[0-9].*", ".so");
                    if (un != b) CreateSymlink(so, Path.Combine(rtLib, un));
                }

                // Curated plugins: skip Qt/CEF/UI plugins that abort in a headless process.
                string pluginSrc = Path.Combine(sysLib, "obs-plugins");
                if (Directory.Exists(pluginSrc))
                {
                    foreach (var so in Directory.GetFiles(pluginSrc, "*.so"))
                    {
                        string b = Path.GetFileName(so);
                        if (IsExcludedPlugin(b)) continue;
                        CreateSymlink(so, Path.Combine(rtPlugins, b));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to build OBS runtime dir: {ex.Message}");
                return null;
            }

            return new RuntimePaths
            {
                LibDir = rtLib,
                ModulePath = rtPlugins,
                ModuleDataPath = Path.Combine(obsData, "obs-plugins", "%module%"),
                DataPath = Path.Combine(obsData, "libobs"),
                // With a system OBS, ffmpeg and gstreamer codecs come from the system too.
                FfmpegDir = null,
                GstPluginDir = null,
            };
        }

        // A bundle may ship ffmpeg at bin/ffmpeg or ffmpeg (root). Returns the directory or null.
        private static string? FindFfmpegDir(string root)
        {
            if (File.Exists(Path.Combine(root, "bin", "ffmpeg"))) return Path.Combine(root, "bin");
            if (File.Exists(Path.Combine(root, "ffmpeg"))) return root;
            return null;
        }

        // A bundle may ship the gstreamer H.264 plugin under lib/gstreamer-1.0 or gstreamer-1.0.
        private static string? FindGstDir(string root)
        {
            foreach (var d in new[] { Path.Combine(root, "lib", "gstreamer-1.0"), Path.Combine(root, "gstreamer-1.0") })
                if (Directory.Exists(d)) return d;
            return null;
        }

        private static bool IsExcludedPlugin(string name) =>
            name is "frontend-tools.so" or "obs-websocket.so" or "obs-browser.so"
            || name.StartsWith("decklink") || name.EndsWith("-ui.so");

        private static string? FindFirstDir(string[] dirs, Func<string, bool> predicate)
        {
            foreach (var d in dirs)
                if (Directory.Exists(d) && predicate(d)) return d;
            return null;
        }

        private static void CreateSymlink(string target, string linkPath)
        {
            try
            {
                if (File.Exists(linkPath) || Directory.Exists(linkPath)) File.Delete(linkPath);
                File.CreateSymbolicLink(linkPath, target);
            }
            catch (Exception ex)
            {
                Log.Warning($"Symlink {linkPath} -> {target} failed: {ex.Message}");
            }
        }
    }
}
