using Serilog;
using System.Diagnostics;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Platform.Linux
{
    /// <summary>Linux has no portable, cross-desktop tray API this milestone, so the tray is a no-op.</summary>
    internal sealed class LinuxTrayIcon : ITrayIcon
    {
        public void Initialize(Action onOpen, Action onExit) { }
        public void SetRecording(bool recording) { }
    }

    /// <summary>A watcher that never fires. Used where Linux has no change-notification source yet.</summary>
    internal sealed class NoopWatcher : IPlatformWatcher
    {
        public event Action Changed { add { } remove { } }
        public void Dispose() { }
    }

    internal sealed class LinuxAudioDeviceService : IAudioDeviceService
    {
        public List<AudioDevice> GetInputDevices() => Enumerate("sources");
        public List<AudioDevice> GetOutputDevices() => Enumerate("sinks");
        public IPlatformWatcher CreateWatcher() => new NoopWatcher();

        // Enumerate PipeWire/PulseAudio endpoints via `pactl`. The first entry is always a
        // "Default" pseudo-device (id "default") so OBS's *_capture FromDefault path works even
        // when pactl is unavailable.
        private static List<AudioDevice> Enumerate(string kind)
        {
            var devices = new List<AudioDevice>
            {
                new AudioDevice { Id = "default", Name = "Default", IsDefault = true }
            };

            try
            {
                string output = LinuxProcess.RunCapture("pactl", $"list short {kind}");
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    // Format: <index>\t<name>\t<driver>\t<sample-spec>\t<state>
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    string name = parts[1].Trim();
                    if (string.IsNullOrEmpty(name) || devices.Any(d => d.Id == name)) continue;
                    devices.Add(new AudioDevice { Id = name, Name = name, IsDefault = false });
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"pactl audio enumeration failed (using default only): {ex.Message}");
            }

            return devices;
        }
    }

    internal sealed class LinuxDisplayService : IDisplayService
    {
        private const int DefaultHeight = 1080;
        private const int DefaultWidth = 1920;

        // Cache the last enumerated geometry so the resolution/height queries don't re-run xrandr.
        private static int _maxHeight = DefaultHeight;
        private static int _primaryWidth = DefaultWidth;
        private static int _primaryHeight = DefaultHeight;

        public bool LoadAvailableMonitorsIntoState()
        {
            var displays = Enumerate();
            var current = AppState.Instance.Displays;

            bool changed = current == null || !current.SequenceEqual(displays);
            if (changed)
            {
                AppState.Instance.Displays = displays;
                AppState.Instance.MaxDisplayHeight = _maxHeight;
            }
            return changed;
        }

        public bool GetPrimaryMonitorPhysicalResolution(out uint width, out uint height)
        {
            // Ensure geometry is populated at least once.
            if (AppState.Instance.Displays == null || AppState.Instance.Displays.Count == 0)
                Enumerate();
            width = (uint)_primaryWidth;
            height = (uint)_primaryHeight;
            return true;
        }

        public bool HasDisplayWithMinHeight(int minHeight)
        {
            if (AppState.Instance.Displays == null || AppState.Instance.Displays.Count == 0)
                Enumerate();
            return _maxHeight >= minHeight;
        }

        public IPlatformWatcher CreateWatcher() => new NoopWatcher();

        // Enumerate connected monitors via xrandr (X11). Falls back to a single 1920x1080 display
        // when xrandr is unavailable (e.g. headless or Wayland without XWayland).
        private static List<Display> Enumerate()
        {
            var displays = new List<Display>();
            int maxHeight = 0, primaryW = 0, primaryH = 0, firstW = 0, firstH = 0;
            try
            {
                string output = LinuxProcess.RunCapture("xrandr", "--query");
                // Lines like: "HDMI-1 connected primary 1920x1080+0+0 (normal ...) 520mm x 290mm"
                var rx = new System.Text.RegularExpressions.Regex(
                    @"^(?<name>\S+)\s+connected\s+(?<primary>primary\s+)?(?<w>\d+)x(?<h>\d+)\+");
                foreach (var line in output.Split('\n'))
                {
                    var m = rx.Match(line.Trim());
                    if (!m.Success) continue;
                    string name = m.Groups["name"].Value;
                    bool isPrimary = m.Groups["primary"].Success;
                    int w = int.Parse(m.Groups["w"].Value);
                    int h = int.Parse(m.Groups["h"].Value);
                    displays.Add(new Display { DeviceName = name, DeviceId = name, IsPrimary = isPrimary, IsHdr = false });
                    if (h > maxHeight) maxHeight = h;
                    if (firstW == 0) { firstW = w; firstH = h; }
                    if (isPrimary) { primaryW = w; primaryH = h; }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"xrandr display enumeration failed: {ex.Message}");
            }

            if (displays.Count == 0)
            {
                displays.Add(new Display { DeviceName = "Display", DeviceId = "default", IsPrimary = true, IsHdr = false });
                maxHeight = DefaultHeight; primaryW = DefaultWidth; primaryH = DefaultHeight;
            }
            else if (primaryW == 0)
            {
                // No monitor flagged "primary"; fall back to the first connected monitor's geometry.
                primaryW = firstW; primaryH = firstH;
            }

            _maxHeight = maxHeight > 0 ? maxHeight : DefaultHeight;
            _primaryWidth = primaryW > 0 ? primaryW : DefaultWidth;
            _primaryHeight = primaryH > 0 ? primaryH : DefaultHeight;
            return displays;
        }
    }

    internal sealed class LinuxNativeDialogs : INativeDialogs
    {
        public async Task<string?> PickFolderAsync(string description)
        {
            string result = await LinuxProcess.RunCaptureAsync("zenity",
                $"--file-selection --directory --title=\"{Escape(description)}\"");
            result = result.Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        public async Task<string?> PickFileAsync(string title, string filterDescription, string extension)
        {
            string result = await LinuxProcess.RunCaptureAsync("zenity",
                $"--file-selection --title=\"{Escape(title)}\" --file-filter=\"{Escape(filterDescription)} | *.{extension}\"");
            result = result.Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        public async Task<string[]?> PickFilesAsync(string title, string filterDescription, string extension)
        {
            string result = await LinuxProcess.RunCaptureAsync("zenity",
                $"--file-selection --multiple --separator=\"|\" --title=\"{Escape(title)}\" --file-filter=\"{Escape(filterDescription)} | *.{extension}\"");
            result = result.Trim();
            if (string.IsNullOrEmpty(result)) return null;
            return result.Split('|', StringSplitOptions.RemoveEmptyEntries);
        }

        public void OpenFileLocation(string filePath)
        {
            // Selecting a file in the manager is desktop-specific; open its containing folder instead.
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                LinuxProcess.Start("xdg-open", $"\"{dir}\"");
        }

        public void OpenUrl(string url) => LinuxProcess.Start("xdg-open", $"\"{url}\"");

        public void CopyFileToClipboard(string filePath)
        {
            try
            {
                // Best-effort: put a file:// URI on the clipboard (works with most GTK/Qt managers).
                var uri = "file://" + filePath;
                var psi = new ProcessStartInfo("xclip", "-selection clipboard -t text/uri-list")
                {
                    RedirectStandardInput = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.StandardInput.Write(uri);
                    proc.StandardInput.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to copy file to clipboard (xclip): {ex.Message}");
            }
        }

        private static string Escape(string s) => s.Replace("\"", "\\\"");
    }

    internal sealed class LinuxStartupManager : IStartupManager
    {
        private static string DesktopFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), // ~/.config
                "autostart", "segra.desktop");

        public void SetStartupStatus(bool enable)
        {
            try
            {
                string path = DesktopFilePath;
                if (enable)
                {
                    string exePath = Environment.ProcessPath ?? "";
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    string contents =
                        "[Desktop Entry]\n" +
                        "Type=Application\n" +
                        "Name=Segra\n" +
                        $"Exec=\"{exePath}\" --from-startup\n" +
                        "X-GNOME-Autostart-enabled=true\n" +
                        "Terminal=false\n";
                    File.WriteAllText(path, contents);
                    Log.Information("Added Segra to autostart (.desktop)");
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    Log.Information("Removed Segra from autostart");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
            }
        }

        public bool GetStartupStatus()
        {
            try { return File.Exists(DesktopFilePath); }
            catch (Exception ex) { Log.Error(ex.Message); return false; }
        }
    }

    internal sealed class LinuxSoundPlayer : ISoundPlayer
    {
        // paplay plays the WAV; volume maps to PulseAudio's 0-65536 scale (65536 = 100%).
        public void Play(byte[] wavData, float volume)
        {
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"segra_sfx_{Guid.NewGuid():N}.wav");
                File.WriteAllBytes(tempPath, wavData);

                var psi = new ProcessStartInfo("paplay", $"--volume={(int)(Math.Clamp(volume, 0f, 1f) * 65536)} \"{tempPath}\"")
                {
                    UseShellExecute = false
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit(5000);

                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to play sound via paplay: {ex.Message}");
            }
        }
    }

    /// <summary>Small helpers for launching Linux CLI tools.</summary>
    internal static class LinuxProcess
    {
        public static void Start(string file, string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to start '{file} {args}': {ex.Message}");
            }
        }

        public static string RunCapture(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            // Drain stderr concurrently, or a tool that fills the stderr pipe buffer would deadlock.
            var errTask = proc.StandardError.ReadToEndAsync();
            string output = proc.StandardOutput.ReadToEnd();
            errTask.GetAwaiter().GetResult();
            proc.WaitForExit(10000);
            return output;
        }

        public static async Task<string> RunCaptureAsync(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var errTask = proc.StandardError.ReadToEndAsync();
            string output = await proc.StandardOutput.ReadToEndAsync();
            await errTask;
            await proc.WaitForExitAsync();
            return output;
        }
    }
}
