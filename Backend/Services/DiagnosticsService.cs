using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Windows.Storage;
using Serilog;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Segra.Backend.Services
{
    internal static class DiagnosticsService
    {
        public static void LogSnapshot()
        {
            try
            {
                Log.Information("============ DIAGNOSTIC SNAPSHOT ============");

                LogAppAndRuntime();
                LogSystemHardware();
                LogDrives();
                LogRecordingAndEncoding();
                LogAudio();
                LogDisplay();
                LogStorage();
                LogGameDetection();
                LogAccountAndMisc();
                LogClipExport();
                LogRecentErrors();

                Log.Information("============ END DIAGNOSTIC SNAPSHOT ============");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to write diagnostic snapshot: {ex.Message}");
            }
        }

        private static void LogAppAndRuntime()
        {
            Log.Information("--- App & runtime ---");
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Log.Information($"App version: {version}");
#if DEBUG
            Log.Information("Build configuration: Debug");
#else
            Log.Information("Build configuration: Release");
#endif
            Log.Information($"Is first run: {Program.IsFirstRun}");
            Log.Information($"OS version: {Environment.OSVersion.VersionString}");
            Log.Information($"OS description: {RuntimeInformation.OSDescription}");
            Log.Information($"OS architecture: {RuntimeInformation.OSArchitecture}");
            Log.Information($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
            Log.Information($".NET runtime: {RuntimeInformation.FrameworkDescription}");
            Log.Information($"Processor count: {Environment.ProcessorCount}");

            using var process = Process.GetCurrentProcess();
            Log.Information($"Working set: {process.WorkingSet64 / (1024 * 1024)} MiB");
            Log.Information($"Private memory: {process.PrivateMemorySize64 / (1024 * 1024)} MiB");
            Log.Information($"Process uptime: {DateTime.Now - process.StartTime}");
            Log.Information($"System uptime: {TimeSpan.FromMilliseconds(Environment.TickCount64)}");
            Log.Information($"UTC time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            Log.Information($"Local time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log.Information($"Time zone: {TimeZoneInfo.Local.Id} (UTC offset {TimeZoneInfo.Local.BaseUtcOffset})");
        }

        private static void LogSystemHardware()
        {
            Log.Information("--- System hardware ---");

            try
            {
                using var cpuSearcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
                foreach (System.Management.ManagementObject cpu in cpuSearcher.Get())
                {
                    Log.Information($"CPU: {cpu["Name"]} ({cpu["NumberOfCores"]} cores, {cpu["NumberOfLogicalProcessors"]} logical, {cpu["MaxClockSpeed"]} MHz)");
                }
            }
            catch (Exception ex)
            {
                Log.Information($"CPU: error reading ({ex.Message})");
            }

            try
            {
                using var memSearcher = new System.Management.ManagementObjectSearcher(
                    "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (System.Management.ManagementObject mem in memSearcher.Get())
                {
                    if (mem["TotalPhysicalMemory"] is { } raw)
                    {
                        ulong bytes = Convert.ToUInt64(raw);
                        double gb = bytes / 1024.0 / 1024.0 / 1024.0;
                        Log.Information($"Total physical memory: {gb:F2} GB");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Information($"Total physical memory: error reading ({ex.Message})");
            }

            try
            {
                using var gpuSearcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM, DriverVersion, DriverDate, VideoModeDescription, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
                var gpus = gpuSearcher.Get().Cast<System.Management.ManagementObject>().ToList();
                Log.Information($"GPU adapters ({gpus.Count}):");
                foreach (var gpu in gpus)
                {
                    string name = gpu["Name"]?.ToString() ?? "<unknown>";
                    string driver = gpu["DriverVersion"]?.ToString() ?? "<unknown>";
                    string driverDate = gpu["DriverDate"]?.ToString() ?? "<unknown>";
                    string vramStr = "<unknown>";
                    if (gpu["AdapterRAM"] is { } ramObj)
                    {
                        // Win32_VideoController.AdapterRAM is uint32; values >4 GiB are clamped.
                        uint vramBytes = Convert.ToUInt32(ramObj);
                        vramStr = $"{vramBytes / (1024 * 1024)} MiB";
                    }
                    string resStr = "";
                    if (gpu["CurrentHorizontalResolution"] != null && gpu["CurrentVerticalResolution"] != null)
                    {
                        resStr = $", mode={gpu["CurrentHorizontalResolution"]}x{gpu["CurrentVerticalResolution"]}@{gpu["CurrentRefreshRate"]}Hz";
                    }
                    Log.Information($"  - {name} (driver={driver}, driverDate={driverDate}, vramReported={vramStr}{resStr})");
                }
            }
            catch (Exception ex)
            {
                Log.Information($"GPU adapters: error reading ({ex.Message})");
            }
        }

        private static void LogDrives()
        {
            Log.Information("--- Drives ---");

            // Physical disks via MSFT_PhysicalDisk (accurate media/bus type, uint64 size)
            try
            {
                using var physSearcher = new System.Management.ManagementObjectSearcher(
                    @"\\.\root\Microsoft\Windows\Storage",
                    "SELECT FriendlyName, Model, Size, MediaType, BusType, SpindleSpeed FROM MSFT_PhysicalDisk");
                var disks = physSearcher.Get().Cast<System.Management.ManagementObject>().ToList();
                Log.Information($"Physical disks ({disks.Count}):");
                foreach (var disk in disks)
                {
                    string name = disk["FriendlyName"]?.ToString() ?? disk["Model"]?.ToString() ?? "<unknown>";
                    string sizeStr = "<unknown>";
                    if (disk["Size"] is { } sizeObj)
                    {
                        double sizeGb = Convert.ToUInt64(sizeObj) / 1024.0 / 1024.0 / 1024.0;
                        sizeStr = $"{sizeGb:F2} GB";
                    }
                    string media = disk["MediaType"] is { } m ? MediaTypeName(Convert.ToUInt16(m)) : "<unknown>";
                    string bus = disk["BusType"] is { } b ? BusTypeName(Convert.ToUInt16(b)) : "<unknown>";
                    string spindleStr = "";
                    if (disk["SpindleSpeed"] is { } sp)
                    {
                        uint rpm = Convert.ToUInt32(sp);
                        if (rpm > 0 && rpm != uint.MaxValue) spindleStr = $", {rpm} RPM";
                    }
                    Log.Information($"  - {name} (size={sizeStr}, media={media}, bus={bus}{spindleStr})");
                }
            }
            catch (Exception ex)
            {
                // MSFT_PhysicalDisk may be unavailable on some Windows versions; fall back to Win32_DiskDrive.
                Log.Information($"MSFT_PhysicalDisk unavailable ({ex.Message}), falling back to Win32_DiskDrive");
                try
                {
                    using var fallback = new System.Management.ManagementObjectSearcher(
                        "SELECT Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive");
                    var disks = fallback.Get().Cast<System.Management.ManagementObject>().ToList();
                    Log.Information($"Physical disks ({disks.Count}):");
                    foreach (var disk in disks)
                    {
                        string model = disk["Model"]?.ToString() ?? "<unknown>";
                        string sizeStr = "<unknown>";
                        if (disk["Size"] is { } sizeObj)
                        {
                            double sizeGb = Convert.ToUInt64(sizeObj) / 1024.0 / 1024.0 / 1024.0;
                            sizeStr = $"{sizeGb:F2} GB";
                        }
                        string iface = disk["InterfaceType"]?.ToString() ?? "<unknown>";
                        string media = disk["MediaType"]?.ToString() ?? "<unknown>";
                        Log.Information($"  - {model} (size={sizeStr}, interface={iface}, media={media})");
                    }
                }
                catch (Exception ex2)
                {
                    Log.Information($"Physical disks: error reading ({ex2.Message})");
                }
            }

            // Logical drives via Win32_LogicalDisk (per-volume free/total/filesystem/label)
            try
            {
                using var logicalSearcher = new System.Management.ManagementObjectSearcher(
                    "SELECT DeviceID, DriveType, FileSystem, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk");
                var volumes = logicalSearcher.Get().Cast<System.Management.ManagementObject>().ToList();
                Log.Information($"Logical drives ({volumes.Count}):");
                foreach (var vol in volumes)
                {
                    string id = vol["DeviceID"]?.ToString() ?? "<unknown>";
                    string type = vol["DriveType"] is { } dt ? DriveTypeName(Convert.ToUInt32(dt)) : "<unknown>";
                    string fs = vol["FileSystem"]?.ToString() ?? "<none>";
                    string label = vol["VolumeName"]?.ToString() ?? "";
                    string usageStr = "<unknown>";
                    if (vol["Size"] is { } sizeObj && vol["FreeSpace"] is { } freeObj)
                    {
                        ulong size = Convert.ToUInt64(sizeObj);
                        ulong free = Convert.ToUInt64(freeObj);
                        double sizeGb = size / 1024.0 / 1024.0 / 1024.0;
                        double freeGb = free / 1024.0 / 1024.0 / 1024.0;
                        double usedGb = (size - free) / 1024.0 / 1024.0 / 1024.0;
                        double pct = size > 0 ? (size - free) * 100.0 / size : 0;
                        usageStr = $"used={usedGb:F2} GB / total={sizeGb:F2} GB ({pct:F1}%), free={freeGb:F2} GB";
                    }
                    Log.Information($"  - {id} ({type}, {fs}, label=\"{label}\") {usageStr}");
                }
            }
            catch (Exception ex)
            {
                Log.Information($"Logical drives: error reading ({ex.Message})");
            }
        }

        private static string MediaTypeName(ushort code) => code switch
        {
            0 => "Unspecified",
            3 => "HDD",
            4 => "SSD",
            5 => "SCM",
            _ => $"Unknown({code})"
        };

        private static string BusTypeName(ushort code) => code switch
        {
            0 => "Unknown",
            1 => "SCSI",
            2 => "ATAPI",
            3 => "ATA",
            4 => "1394",
            5 => "SSA",
            6 => "FibreChannel",
            7 => "USB",
            8 => "RAID",
            9 => "iSCSI",
            10 => "SAS",
            11 => "SATA",
            12 => "SD",
            13 => "MMC",
            14 => "Virtual",
            15 => "FileBackedVirtual",
            16 => "StorageSpaces",
            17 => "NVMe",
            18 => "MicrosoftReserved",
            19 => "SCM",
            _ => $"Unknown({code})"
        };

        private static readonly Regex LogHeaderRegex = new(
            @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(\w{3})\] ",
            RegexOptions.Compiled);

        private static void LogRecentErrors()
        {
            const int maxEntries = 50;
            var cutoff = DateTimeOffset.Now.AddHours(-24);
            Log.Information($"--- Recent errors (last 24h, max {maxEntries}) ---");

            try
            {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Segra", "logs.log");

                if (!File.Exists(logPath))
                {
                    Log.Information("Log file not found");
                    return;
                }

                var entries = new List<List<string>>();
                List<string>? currentEntry = null;

                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var match = LogHeaderRegex.Match(line);
                    if (match.Success)
                    {
                        currentEntry = null;
                        if (match.Groups[2].Value != "ERR") continue;
                        if (!DateTimeOffset.TryParseExact(
                                match.Groups[1].Value,
                                "yyyy-MM-dd HH:mm:ss.fff zzz",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None,
                                out var ts))
                        {
                            continue;
                        }
                        if (ts < cutoff) continue;

                        currentEntry = new List<string> { line };
                        entries.Add(currentEntry);
                    }
                    else if (currentEntry != null)
                    {
                        // Continuation (exception stack trace)
                        currentEntry.Add(line);
                    }
                }

                int total = entries.Count;
                var recent = total > maxEntries
                    ? entries.GetRange(total - maxEntries, maxEntries)
                    : entries;

                Log.Information($"Found {total} error entr{(total == 1 ? "y" : "ies")} in last 24h, showing last {recent.Count}");
                foreach (var entry in recent)
                {
                    foreach (var l in entry)
                    {
                        Log.Information($"  {l}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Information($"Recent errors: error reading log ({ex.Message})");
            }
        }

        private static string DriveTypeName(uint code) => code switch
        {
            0 => "Unknown",
            1 => "NoRootDirectory",
            2 => "Removable",
            3 => "Fixed",
            4 => "Network",
            5 => "CD-ROM",
            6 => "RAMDisk",
            _ => $"Unknown({code})"
        };

        private static void LogRecordingAndEncoding()
        {
            Log.Information("--- Recording & encoding ---");
            var s = Settings.Instance;
            Log.Information($"Selected OBS version: {s.SelectedOBSVersion ?? "automatic"}");
            Log.Information($"Has loaded OBS: {s.State.HasLoadedObs}");
            Log.Information($"Pending OBS update: {s.PendingOBSUpdate}");
            Log.Information($"Available OBS versions: {s.State.AvailableOBSVersions.Count}");

            if (s.State.PreRecording != null)
                Log.Information($"PreRecording: game={s.State.PreRecording.Game}, status={s.State.PreRecording.Status}, pid={s.State.PreRecording.Pid?.ToString() ?? "<none>"}");
            else
                Log.Information("PreRecording: <none>");

            if (s.State.Recording != null)
                Log.Information($"Recording: game={s.State.Recording.Game}, startTime={s.State.Recording.StartTime:O}, hook={s.State.Recording.IsUsingGameHook}, pid={s.State.Recording.Pid?.ToString() ?? "<none>"}");
            else
                Log.Information("Recording: <none>");

            Log.Information($"Encoder: {s.Encoder}");
            if (s.Codec != null)
                Log.Information($"Codec: {s.Codec.FriendlyName} (id={s.Codec.InternalEncoderId}, hw={s.Codec.IsHardwareEncoder})");
            else
                Log.Information("Codec: <null>");
            Log.Information($"Resolution: {s.Resolution} @ {s.FrameRate} fps (stretch4By3={s.Stretch4By3})");
            Log.Information($"Bitrate: {s.Bitrate} Mbps (min={s.MinBitrate}, max={s.MaxBitrate})");
            Log.Information($"Rate control: {s.RateControl} (CRF={s.CrfValue}, CQ={s.CqLevel})");
            Log.Information($"Video quality preset: {s.VideoQualityPreset}");
            Log.Information($"Recording mode: {s.RecordingMode}");
            Log.Information($"Replay buffer: duration={s.ReplayBufferDuration}s, maxSize={s.ReplayBufferMaxSize}MB");
            Log.Information($"Display capture method: {s.DisplayCaptureMethod}");
            Log.Information($"GPU vendor: {s.State.GpuVendor}");
            Log.Information($"CUDA compute capability: {s.State.CudaComputeCapability?.ToString() ?? "<none>"}");
            Log.Information($"Available codecs ({s.State.Codecs.Count}):");
            foreach (var codec in s.State.Codecs)
            {
                Log.Information($"  - {codec.FriendlyName} (id={codec.InternalEncoderId}, hw={codec.IsHardwareEncoder})");
            }
        }

        private static void LogAudio()
        {
            Log.Information("--- Audio ---");
            var s = Settings.Instance;
            Log.Information($"Configured input devices ({s.InputDevices.Count}):");
            foreach (var d in s.InputDevices)
                Log.Information($"  - {d.Name} (id={d.Id}, volume={d.Volume:F2})");
            Log.Information($"Configured output devices ({s.OutputDevices.Count}):");
            foreach (var d in s.OutputDevices)
                Log.Information($"  - {d.Name} (id={d.Id}, volume={d.Volume:F2})");
            Log.Information($"Detected input devices: {s.State.InputDevices.Count}");
            Log.Information($"Detected output devices: {s.State.OutputDevices.Count}");
            Log.Information($"Force mono input: {s.ForceMonoInputSources}");
            Log.Information($"Input noise suppression: {s.InputNoiseSuppression}");
            Log.Information($"Separate audio tracks: {s.EnableSeparateAudioTracks}");
            Log.Information($"Audio output mode: {s.AudioOutputMode}");
        }

        private static void LogDisplay()
        {
            Log.Information("--- Display ---");
            var s = Settings.Instance;
            Log.Information($"Detected displays ({s.State.Displays.Count}):");
            foreach (var d in s.State.Displays)
                Log.Information($"  - {d.DeviceName} (id={d.DeviceId}, primary={d.IsPrimary})");
            if (s.SelectedDisplay != null)
                Log.Information($"Selected display: {s.SelectedDisplay.DeviceName} (primary={s.SelectedDisplay.IsPrimary})");
            else
                Log.Information("Selected display: <auto>");
            Log.Information($"Max display height: {s.State.MaxDisplayHeight}");
        }

        private static void LogStorage()
        {
            Log.Information("--- Storage ---");
            var s = Settings.Instance;
            Log.Information($"Content folder: {s.ContentFolder}");
            LogDriveInfo("Content folder drive", s.ContentFolder);
            Log.Information($"Cache folder: {s.CacheFolder}");
            LogDriveInfo("Cache folder drive", s.CacheFolder);
            Log.Information($"Current content size: {s.State.CurrentFolderSizeGb:F2} GB");
            Log.Information($"Storage limit: {s.StorageLimit} GB");

            var byType = s.State.Content
                .GroupBy(c => c.Type)
                .OrderBy(g => g.Key.ToString())
                .Select(g => $"{g.Key}={g.Count()}");
            Log.Information($"Content count: {s.State.Content.Count} ({string.Join(", ", byType)})");
        }

        private static void LogDriveInfo(string label, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return;
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    Log.Information($"{label}: {root} not ready");
                    return;
                }
                double freeGb = drive.AvailableFreeSpace / (double)StorageService.BYTES_PER_GB;
                double totalGb = drive.TotalSize / (double)StorageService.BYTES_PER_GB;
                Log.Information($"{label}: {root} free={freeGb:F2} GB / total={totalGb:F2} GB ({drive.DriveFormat})");
            }
            catch (Exception ex)
            {
                Log.Information($"{label}: error reading drive info ({ex.Message})");
            }
        }

        private static void LogGameDetection()
        {
            Log.Information("--- Game detection ---");
            var s = Settings.Instance;
            Log.Information($"Whitelist: {s.Whitelist.Count} entries");
            Log.Information($"Blacklist: {s.Blacklist.Count} entries");
            Log.Information($"Game integrations: CS2={s.GameIntegrations.CounterStrike2.Enabled}, LoL={s.GameIntegrations.LeagueOfLegends.Enabled}, PUBG={s.GameIntegrations.Pubg.Enabled}, RocketLeague={s.GameIntegrations.RocketLeague.Enabled}");
        }

        private static void LogAccountAndMisc()
        {
            Log.Information("--- Account & misc settings ---");
            var s = Settings.Instance;
            Log.Information($"Logged in: {s.Auth.HasCredentials()}");
            Log.Information($"Enable AI: {s.EnableAi}");
            Log.Information($"Auto generate highlights: {s.AutoGenerateHighlights}");
            Log.Information($"Run on startup: {s.RunOnStartup}");
            Log.Information($"Receive beta updates: {s.ReceiveBetaUpdates}");
            Log.Information($"Remove original after compression: {s.RemoveOriginalAfterCompression}");
            Log.Information($"Discard sessions without bookmarks: {s.DiscardSessionsWithoutBookmarks}");
            Log.Information($"Theme: {s.Theme}");
        }

        private static void LogClipExport()
        {
            Log.Information("--- Clip export settings ---");
            var s = Settings.Instance;
            Log.Information($"Clip encoder: {s.ClipEncoder}");
            Log.Information($"Clip codec: {s.ClipCodec}");
            Log.Information($"Clip fps: {s.ClipFps}");
            Log.Information($"Clip quality CPU (CRF): {s.ClipQualityCpu}");
            Log.Information($"Clip quality GPU (CQ): {s.ClipQualityGpu}");
            Log.Information($"Clip preset: {s.ClipPreset}");
            Log.Information($"Clip audio quality: {s.ClipAudioQuality}");
            Log.Information($"Clip keep separate audio tracks: {s.ClipKeepSeparateAudioTracks}");
            Log.Information($"Clip quality preset: {s.ClipQualityPreset}");
        }
    }
}
