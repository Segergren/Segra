using Serilog;
using Vortice.DXCore;

namespace Segra.Backend.Utils
{
    public static class GeneralUtils
    {
        public enum GpuVendor
        {
            Unknown,
            Nvidia,
            AMD,
            Intel
        }

        private static readonly List<string> internalGpuIdentifiers = new List<string>
        {
            // Intel integrated GPUs
            "HD Graphics",         // Broadwell (5xxx), Skylake (510–530), Kaby Lake (610/620), Comet Lake, etc.
            "Iris Graphics",       // Skylake Iris 540/550
            "Iris Pro Graphics",   // Broadwell Iris Pro 6200
            "Iris Plus Graphics",  // Kaby Lake / Whiskey Lake
            "UHD Graphics",        // Coffee Lake and newer
            "Iris Xe Graphics",    // Tiger Lake and newer

            // AMD integrated GPUs
            "Radeon R7 Graphics",    // Kaveri / Carrizo APU series
            "Radeon R5 Graphics",    // Kaveri / Carrizo APU series
            "Radeon Vega",           // Raven Ridge / Picasso / Renoir APUs (e.g. Vega 8, Vega 11)
            "Radeon Graphics",       // Zen+ / Zen2 APUs (generic naming on 4000/5000G “Graphics”)
            "Radeon(TM) Graphics"
        };

        // Cache the detected GPU vendor to avoid repeated WMI queries
        private static GpuVendor? _cachedGpuVendor = null;

        public static GpuVendor DetectGpuVendor()
        {
            // Return cached value if available
            if (_cachedGpuVendor.HasValue)
            {
                return _cachedGpuVendor.Value;
            }

            // Try using DXCore first - it's more reliable but requires Windows 10 build 19041 or later
            try
            {
                using var factory = DXCore.DXCoreCreateAdapterFactory<IDXCoreAdapterFactory>();

                Guid[] filter = { DXCore.D3D12_Graphics };
                using var list =
                    factory.CreateAdapterList<IDXCoreAdapterList>(filter);

                var adapters = new List<IDXCoreAdapter>();
                for (uint i = 0; i < list.AdapterCount; ++i)
                {
                    adapters.Add(list.GetAdapter<IDXCoreAdapter>(i));
                }

                foreach (var adapter in adapters)
                {
                    Log.Information(adapter.DriverDescription);
                    Log.Information($"  Vendor : 0x{adapter.HardwareID.VendorID:X4}");
                    Log.Information($"  Device : 0x{adapter.HardwareID.DeviceID:X4}");
                    Log.Information($"  VRAM   : {adapter.DedicatedAdapterMemory / (1024 * 1024)} MiB");
                    Log.Information($"  Integrated: {adapter.IsIntegrated}");
                }

                // Sort adapters: non-integrated first, then by dedicated memory size (largest first)
                var sortedAdapters = adapters
                    .OrderBy(a => a.IsIntegrated) // False comes before True
                    .ThenByDescending(a => a.DedicatedAdapterMemory)
                    .ToList();

                // Process the sorted adapters
                foreach (var adapter in sortedAdapters)
                {
                    string name = adapter.DriverDescription;

                    if (name.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Information($"Detected NVIDIA GPU: {name}");
                        _cachedGpuVendor = GpuVendor.Nvidia;
                        return GpuVendor.Nvidia;
                    }
                    else if (name.Contains("amd", StringComparison.OrdinalIgnoreCase) || name.Contains("radeon", StringComparison.OrdinalIgnoreCase) || name.Contains("ati", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Information($"Detected AMD GPU: {name}");
                        _cachedGpuVendor = GpuVendor.AMD;
                        return GpuVendor.AMD;
                    }
                    else if (name.Contains("intel", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Information($"Detected Intel GPU: {name}");
                        _cachedGpuVendor = GpuVendor.Intel;
                        return GpuVendor.Intel;
                    }
                }

                // Clean up adapters
                foreach (var adapter in adapters)
                {
                    adapter.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error detecting GPU vendor using DXCore: {ex.Message}");
            }

            // Fallback to WMI if DXCore fails
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT * FROM Win32_VideoController WHERE CurrentHorizontalResolution > 0 AND CurrentVerticalResolution > 0"))
                {
                    List<System.Management.ManagementObject> gpus = searcher.Get().Cast<System.Management.ManagementObject>().ToList();

                    // Log all active GPUs found
                    Log.Information($"Found {gpus.Count} active GPU(s):");
                    foreach (var gpu in gpus)
                    {
                        Log.Information($"  - {gpu["Name"]} (Status: {gpu["Status"]}, PNPDeviceID: {gpu["PNPDeviceID"]}, VideoMemoryType: {gpu["VideoMemoryType"]}, RAM: {gpu["AdapterRAM"]}, Driver: {gpu["DriverVersion"]});");
                    }

                    // Sort GPUs - external GPUs first, then internal ones
                    gpus.Sort((a, b) =>
                    {
                        string nameA = a["Name"]?.ToString() ?? string.Empty;
                        string nameB = b["Name"]?.ToString() ?? string.Empty;

                        bool isAInternal = internalGpuIdentifiers.Any(id => nameA.Contains(id, StringComparison.OrdinalIgnoreCase));
                        bool isBInternal = internalGpuIdentifiers.Any(id => nameB.Contains(id, StringComparison.OrdinalIgnoreCase));

                        // External GPUs come first (false before true)
                        return isAInternal.CompareTo(isBInternal);
                    });

                    foreach (var gpu in gpus)
                    {
                        string name = gpu["Name"]?.ToString()?.ToLower() ?? string.Empty;

                        if (name.Contains("nvidia"))
                        {
                            Log.Information($"Detected NVIDIA GPU: {gpu["Name"]}");
                            _cachedGpuVendor = GpuVendor.Nvidia;
                            return GpuVendor.Nvidia;
                        }
                        else if (name.Contains("amd") || name.Contains("radeon") || name.Contains("ati"))
                        {
                            Log.Information($"Detected AMD GPU: {gpu["Name"]}");
                            _cachedGpuVendor = GpuVendor.AMD;
                            return GpuVendor.AMD;
                        }
                        else if (name.Contains("intel"))
                        {
                            Log.Information($"Detected Intel GPU: {gpu["Name"]}");
                            _cachedGpuVendor = GpuVendor.Intel;
                            return GpuVendor.Intel;
                        }
                    }
                }

                // Fallback: check all video controllers if the above didn't find any active ones
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController"))
                {
                    var allGpus = searcher.Get().Cast<System.Management.ManagementObject>().ToList();

                    // Log all GPUs found in fallback search
                    Log.Information($"Found {allGpus.Count} total GPU(s) in fallback search:");
                    foreach (var gpu in allGpus)
                    {
                        Log.Information($"  - {gpu["Name"]} (Status: {gpu["Status"]}, Driver: {gpu["DriverVersion"]});");
                    }

                    foreach (System.Management.ManagementObject gpu in allGpus)
                    {
                        string name = gpu["Name"]?.ToString()?.ToLower() ?? string.Empty;

                        if (name.Contains("nvidia"))
                        {
                            Log.Information($"Detected NVIDIA GPU: {gpu["Name"]}");
                            _cachedGpuVendor = GpuVendor.Nvidia;
                            return GpuVendor.Nvidia;
                        }
                        else if (name.Contains("amd") || name.Contains("radeon") || name.Contains("ati"))
                        {
                            Log.Information($"Detected AMD GPU: {gpu["Name"]}");
                            _cachedGpuVendor = GpuVendor.AMD;
                            return GpuVendor.AMD;
                        }
                        else if (name.Contains("intel"))
                        {
                            Log.Information($"Detected Intel GPU: {gpu["Name"]}");
                            _cachedGpuVendor = GpuVendor.Intel;
                            return GpuVendor.Intel;
                        }
                    }
                }

                Log.Warning("Could not identify GPU vendor, will default to CPU encoding if GPU encoding is selected");
                return GpuVendor.Unknown;
            }
            catch (Exception ex)
            {
                Log.Error($"Error detecting GPU vendor: {ex.Message}");
                return GpuVendor.Unknown;
            }
        }
    }
}
