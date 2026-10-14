using Serilog;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using Segra.Backend.Recorder;
using Segra.Backend.Core.Models;
using Segra.Backend.Windows.Storage;
using System.Runtime.InteropServices;

namespace Segra.Backend.App
{
    internal static class DiagnosticsService
    {
        public static void LogSnapshot()
        {
            try
            {
                var lines = new List<string>();
                LogAppAndRuntime(lines);
                LogSystemHardware(lines);
                LogDrives(lines);
                LogRecordingAndEncoding(lines);
                LogAudio(lines);
                LogDisplay(lines);
                LogStorage(lines);
                LogGameDetection(lines);
                LogAccountAndMisc(lines);
                LogClipExport(lines);

                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
                Log.Information($"Diagnostic snapshot: {encoded}");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to write diagnostic snapshot: {ex.Message}");
            }
        }

        private static void LogAppAndRuntime(List<string> lines)
        {
            lines.Add("--- App & runtime ---");
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            lines.Add($"App version: {version}");
#if DEBUG
            lines.Add("Build configuration: Debug");
#else
            lines.Add("Build configuration: Release");
#endif
            lines.Add($"Is first run: {Program.IsFirstRun}");
            lines.Add($"OS version: {Environment.OSVersion.VersionString}");
            lines.Add($"OS description: {RuntimeInformation.OSDescription}");
            lines.Add($"OS architecture: {RuntimeInformation.OSArchitecture}");
            lines.Add($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
            lines.Add($".NET runtime: {RuntimeInformation.FrameworkDescription}");
            lines.Add($"Processor count: {Environment.ProcessorCount}");

            using var process = Process.GetCurrentProcess();
            lines.Add($"Working set: {process.WorkingSet64 / (1024 * 1024)} MiB");
            lines.Add($"Private memory: {process.PrivateMemorySize64 / (1024 * 1024)} MiB");
            lines.Add($"Process uptime: {DateTime.Now - process.StartTime}");
            lines.Add($"System uptime: {TimeSpan.FromMilliseconds(Environment.TickCount64)}");
            lines.Add($"UTC time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            lines.Add($"Local time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            lines.Add($"Time zone: {TimeZoneInfo.Local.Id} (UTC offset {TimeZoneInfo.Local.BaseUtcOffset})");
        }

        private static void LogSystemHardware(List<string> lines)
        {
            lines.Add("--- System hardware ---");

#if !WINDOWS
            // Linux: read CPU/memory from procfs; GPU vendor from the detection helper.
            try
            {
                if (File.Exists("/proc/cpuinfo"))
                {
                    var model = File.ReadLines("/proc/cpuinfo")
                        .FirstOrDefault(l => l.StartsWith("model name"));
                    if (model != null)
                        lines.Add($"CPU: {model.Split(':', 2).Last().Trim()} ({Environment.ProcessorCount} logical)");
                }
                if (File.Exists("/proc/meminfo"))
                {
                    var memTotal = File.ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemTotal"));
                    if (memTotal != null && long.TryParse(new string(memTotal.Where(char.IsDigit).ToArray()), out long kb))
                        lines.Add($"Total physical memory: {kb / 1024.0 / 1024.0:F2} GB");
                }
                lines.Add($"GPU vendor: {Shared.GeneralUtils.DetectGpuVendor()}");
            }
            catch (Exception ex)
            {
                lines.Add($"System hardware: error reading ({ex.Message})");
            }
            return;
#else
            try
            {
                using var cpuSearcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
                foreach (System.Management.ManagementObject cpu in cpuSearcher.Get())
                {
                    lines.Add($"CPU: {cpu["Name"]} ({cpu["NumberOfCores"]} cores, {cpu["NumberOfLogicalProcessors"]} logical, {cpu["MaxClockSpeed"]} MHz)");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"CPU: error reading ({ex.Message})");
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
                        lines.Add($"Total physical memory: {gb:F2} GB");
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add($"Total physical memory: error reading ({ex.Message})");
            }

            try
            {
                using var gpuSearcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM, DriverVersion, DriverDate, VideoModeDescription, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
                var gpus = gpuSearcher.Get().Cast<System.Management.ManagementObject>().ToList();
                lines.Add($"GPU adapters ({gpus.Count}):");
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
                    lines.Add($"  - {name} (driver={driver}, driverDate={driverDate}, vramReported={vramStr}{resStr})");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"GPU adapters: error reading ({ex.Message})");
            }
#endif
        }

        private static void LogDrives(List<string> lines)
        {
            lines.Add("--- Drives ---");

#if !WINDOWS
            // Linux: enumerate mounted drives via the cross-platform DriveInfo API.
            try
            {
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
                lines.Add($"Drives ({drives.Count}):");
                foreach (var d in drives)
                {
                    double totalGb = d.TotalSize / 1024.0 / 1024.0 / 1024.0;
                    double freeGb = d.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
                    lines.Add($"  - {d.Name} ({d.DriveType}, {d.DriveFormat}) total={totalGb:F2} GB, free={freeGb:F2} GB");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"Drives: error reading ({ex.Message})");
            }
            return;
#else
            // Physical disks via MSFT_PhysicalDisk (accurate media/bus type, uint64 size)
            try
            {
                using var physSearcher = new System.Management.ManagementObjectSearcher(
                    @"\\.\root\Microsoft\Windows\Storage",
                    "SELECT FriendlyName, Model, Size, MediaType, BusType, SpindleSpeed FROM MSFT_PhysicalDisk");
                var disks = physSearcher.Get().Cast<System.Management.ManagementObject>().ToList();
                lines.Add($"Physical disks ({disks.Count}):");
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
                    lines.Add($"  - {name} (size={sizeStr}, media={media}, bus={bus}{spindleStr})");
                }
            }
            catch (Exception ex)
            {
                // MSFT_PhysicalDisk may be unavailable on some Windows versions; fall back to Win32_DiskDrive.
                lines.Add($"MSFT_PhysicalDisk unavailable ({ex.Message}), falling back to Win32_DiskDrive");
                try
                {
                    using var fallback = new System.Management.ManagementObjectSearcher(
                        "SELECT Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive");
                    var disks = fallback.Get().Cast<System.Management.ManagementObject>().ToList();
                    lines.Add($"Physical disks ({disks.Count}):");
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
                        lines.Add($"  - {model} (size={sizeStr}, interface={iface}, media={media})");
                    }
                }
                catch (Exception ex2)
                {
                    lines.Add($"Physical disks: error reading ({ex2.Message})");
                }
            }

            // Logical drives via Win32_LogicalDisk (per-volume free/total/filesystem/label)
            try
            {
                using var logicalSearcher = new System.Management.ManagementObjectSearcher(
                    "SELECT DeviceID, DriveType, FileSystem, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk");
                var volumes = logicalSearcher.Get().Cast<System.Management.ManagementObject>().ToList();
                lines.Add($"Logical drives ({volumes.Count}):");
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
                    lines.Add($"  - {id} ({type}, {fs}, label=\"{label}\") {usageStr}");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"Logical drives: error reading ({ex.Message})");
            }
#endif
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

        private static void LogRecordingAndEncoding(List<string> lines)
        {
            lines.Add("--- Recording & encoding ---");
            var s = Settings.Instance;
            lines.Add($"Selected OBS version: {s.SelectedOBSVersion ?? "automatic"}");
            lines.Add($"Has loaded OBS: {AppState.Instance.HasLoadedObs}");
            lines.Add($"Pending OBS update: {s.PendingOBSUpdate}");
            lines.Add($"Available OBS versions: {AppState.Instance.AvailableOBSVersions.Count}");

            if (AppState.Instance.PreRecording != null)
                lines.Add($"PreRecording: game={AppState.Instance.PreRecording.Game}, status={AppState.Instance.PreRecording.Status}, pid={AppState.Instance.PreRecording.Pid?.ToString() ?? "<none>"}");
            else
                lines.Add("PreRecording: <none>");

            if (AppState.Instance.Recording != null)
                lines.Add($"Recording: game={AppState.Instance.Recording.Game}, startTime={AppState.Instance.Recording.StartTime:O}, hook={AppState.Instance.Recording.IsUsingGameHook}, windowCapture={AppState.Instance.Recording.IsUsingWindowCapture}, pid={AppState.Instance.Recording.Pid?.ToString() ?? "<none>"}");
            else
                lines.Add("Recording: <none>");

            lines.Add($"Encoder: {s.Encoder}");
            if (s.Codec != null)
                lines.Add($"Codec: {s.Codec.FriendlyName} (id={s.Codec.InternalEncoderId}, hw={s.Codec.IsHardwareEncoder})");
            else
                lines.Add("Codec: <null>");
            lines.Add($"Resolution: {s.Resolution} @ {s.FrameRate} fps (stretch4By3={s.Stretch4By3})");
            lines.Add($"Bitrate: {s.Bitrate} Mbps (min={s.MinBitrate}, max={s.MaxBitrate})");
            lines.Add($"Rate control: {s.RateControl} (CRF={s.CrfValue}, CQ={s.CqLevel})");
            lines.Add($"Video quality preset: {s.VideoQualityPreset}");
            lines.Add($"Recording mode: {s.RecordingMode}");
            lines.Add($"Replay buffer: duration={s.ReplayBufferDuration}s, maxSize={s.ReplayBufferMaxSize}MB, alwaysOn={s.AlwaysOnReplayBuffer} (active={AppState.Instance.AlwaysOnBufferActive})");
            lines.Add($"GPU vendor: {AppState.Instance.GpuVendor}");
#if WINDOWS
            lines.Add($"NVENC capabilities: {NvencCapsService.GetCapsSummaryOrNull() ?? "<none>"}");
#endif
            lines.Add($"Available codecs ({AppState.Instance.Codecs.Count}):");
            foreach (var codec in AppState.Instance.Codecs)
            {
                lines.Add($"  - {codec.FriendlyName} (id={codec.InternalEncoderId}, hw={codec.IsHardwareEncoder})");
            }
        }

        private static void LogAudio(List<string> lines)
        {
            lines.Add("--- Audio ---");
            var s = Settings.Instance;
            lines.Add($"Configured input devices ({s.InputDevices.Count}):");
            foreach (var d in s.InputDevices)
                lines.Add($"  - {d.Name} (id={d.Id}, volume={d.Volume:F2}, noiseSuppression={d.NoiseSuppression}, forceMono={d.ForceMono})");
            lines.Add($"Configured output devices ({s.OutputDevices.Count}):");
            foreach (var d in s.OutputDevices)
                lines.Add($"  - {d.Name} (id={d.Id}, volume={d.Volume:F2})");
            lines.Add($"Detected input devices: {AppState.Instance.InputDevices.Count}");
            lines.Add($"Detected output devices: {AppState.Instance.OutputDevices.Count}");
            lines.Add($"Separate audio tracks: {s.EnableSeparateAudioTracks}");
            lines.Add($"Audio output mode: {s.AudioOutputMode}");
        }

        private static void LogDisplay(List<string> lines)
        {
            lines.Add("--- Display ---");
            var s = Settings.Instance;
            lines.Add($"Detected displays ({AppState.Instance.Displays.Count}):");
            foreach (var d in AppState.Instance.Displays)
                lines.Add($"  - {d.DeviceName} (id={d.DeviceId}, primary={d.IsPrimary})");
            if (s.SelectedDisplay != null)
                lines.Add($"Selected display: {s.SelectedDisplay.DeviceName} (primary={s.SelectedDisplay.IsPrimary})");
            else
                lines.Add("Selected display: <auto>");
            lines.Add($"Max display height: {AppState.Instance.MaxDisplayHeight}");
        }

        private static void LogStorage(List<string> lines)
        {
            lines.Add("--- Storage ---");
            var s = Settings.Instance;
            lines.Add($"Content folder: {s.ContentFolder}");
            LogDriveInfo(lines, "Content folder drive", s.ContentFolder);
            lines.Add($"Cache folder: {s.CacheFolder}");
            LogDriveInfo(lines, "Cache folder drive", s.CacheFolder);
            lines.Add($"Current content size: {AppState.Instance.CurrentFolderSizeGb:F2} GB");
            lines.Add($"Storage limit: {s.StorageLimit} GB");

            var byType = AppState.Instance.Content
                .GroupBy(c => c.Type)
                .OrderBy(g => g.Key.ToString())
                .Select(g => $"{g.Key}={g.Count()}");
            lines.Add($"Content count: {AppState.Instance.Content.Count} ({string.Join(", ", byType)})");
        }

        private static void LogDriveInfo(List<string> lines, string label, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                var root = StorageService.GetDriveRoot(path);
                if (string.IsNullOrEmpty(root)) return;
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    lines.Add($"{label}: {root} not ready");
                    return;
                }
                double freeGb = drive.AvailableFreeSpace / (double)StorageService.BYTES_PER_GB;
                double totalGb = drive.TotalSize / (double)StorageService.BYTES_PER_GB;
                lines.Add($"{label}: {root} free={freeGb:F2} GB / total={totalGb:F2} GB ({drive.DriveFormat})");
            }
            catch (Exception ex)
            {
                lines.Add($"{label}: error reading drive info ({ex.Message})");
            }
        }

        private static void LogGameDetection(List<string> lines)
        {
            lines.Add("--- Game detection ---");
            var s = Settings.Instance;
            lines.Add($"Custom game settings: {s.Games.Count} entries ({s.Games.Count(g => g.Record)} recording, {s.Games.Count(g => !g.Record)} blocked)");
            lines.Add($"Game integrations: CS2={s.GameIntegrations.CounterStrike2.Enabled}, LoL={s.GameIntegrations.LeagueOfLegends.Enabled}, PUBG={s.GameIntegrations.Pubg.Enabled}, RocketLeague={s.GameIntegrations.RocketLeague.Enabled}");
        }

        private static void LogAccountAndMisc(List<string> lines)
        {
            lines.Add("--- Account & misc settings ---");
            var s = Settings.Instance;
            lines.Add($"Logged in: {s.Auth.HasCredentials()}");
            lines.Add($"Enable AI: {s.EnableAi}");
            lines.Add($"Auto generate highlights: {s.AutoGenerateHighlights}");
            lines.Add($"Run on startup: {s.RunOnStartup}");
            lines.Add($"Receive beta updates: {s.ReceiveBetaUpdates}");
            lines.Add($"Auto install updates: {s.AutoInstallUpdates}");
            lines.Add($"Confirm before deleting: {s.ConfirmBeforeDeleting}");
            lines.Add($"Remove original after compression: {s.RemoveOriginalAfterCompression}");
            lines.Add($"Discard sessions without bookmarks: {s.DiscardSessionsWithoutBookmarks}");
        }

        private static void LogClipExport(List<string> lines)
        {
            lines.Add("--- Clip export settings ---");
            var s = Settings.Instance;
            lines.Add($"Clip encoder: {s.ClipEncoder}");
            lines.Add($"Clip codec: {s.ClipCodec}");
            lines.Add($"Clip fps: {s.ClipFps}");
            lines.Add($"Clip quality CPU (CRF): {s.ClipQualityCpu}");
            lines.Add($"Clip quality GPU (CQ): {s.ClipQualityGpu}");
            lines.Add($"Clip preset: {s.ClipPreset}");
            lines.Add($"Clip audio quality: {s.ClipAudioQuality}");
            lines.Add($"Clip keep separate audio tracks: {s.ClipKeepSeparateAudioTracks}");
            lines.Add($"Clip quality preset: {s.ClipQualityPreset}");
        }
    }
}
