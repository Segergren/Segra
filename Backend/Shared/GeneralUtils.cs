using Serilog;
using Vortice.DXCore;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Segra.Backend.Shared
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

        private static readonly List<string> internalGpuIdentifiers = new()
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
        private static GpuVendor? _cachedGpuVendor;

        public static GpuVendor DetectGpuVendor()
        {
            if (_cachedGpuVendor.HasValue)
            {
                return _cachedGpuVendor.Value;
            }

            // Try DXCore first - more reliable but requires Windows 10 build 19041 or later
            try
            {
                using var factory = DXCore.DXCoreCreateAdapterFactory<IDXCoreAdapterFactory>();

                Guid[] filter = { DXCore.D3D12_Graphics };
                using var list =
                    factory.CreateAdapterList<IDXCoreAdapterList>(filter);

                var adapters = new List<IDXCoreAdapter>();
                for (uint i = 0; i < list.AdapterCount; ++i)
                {
                    if (list.GetAdapter<IDXCoreAdapter>(i).DedicatedAdapterMemory > 0)
                    {
                        adapters.Add(list.GetAdapter<IDXCoreAdapter>(i));
                    }
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

        private static readonly string[] SensitiveProperties =
        [
            "accesstoken",
            "refreshtoken",
            "jwt",
            "state"
        ];

        public static string RedactSensitiveInfo(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;

            foreach (var prop in SensitiveProperties)
            {
                // Redact string values: "prop":"value"
                var stringPattern = $"\"{prop}\":\"([^\"]+)\"";
                message = Regex.Replace(message, stringPattern, $"\"{prop}\":\"-REDACTED-\"", RegexOptions.IgnoreCase);

                // Redact object/array values: "prop":{...} or "prop":[...]
                // Find the property and then skip to the matching closing brace/bracket
                var propPattern = $"\"{prop}\":";
                var index = message.IndexOf($"\"{prop}\":", StringComparison.OrdinalIgnoreCase);

                while (index >= 0)
                {
                    var valueStart = index + propPattern.Length;
                    if (valueStart < message.Length)
                    {
                        var firstChar = message[valueStart];
                        if (firstChar == '{' || firstChar == '[')
                        {
                            var endIndex = FindMatchingBracket(message, valueStart);
                            if (endIndex > valueStart)
                            {
                                var before = message.Substring(0, index);
                                var after = message.Substring(endIndex + 1);
                                message = before + $"\"{prop}\":\"-REDACTED-\"" + after;
                            }
                        }
                    }

                    index = message.IndexOf($"\"{prop}\":", index + 1, StringComparison.OrdinalIgnoreCase);
                }
            }

            return message;
        }

        private static readonly Regex UsernamePathRegex = new(@"([\\/]Users[\\/])([^\\/]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string RedactUsername(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            return UsernamePathRegex.Replace(text, "$1<user>");
        }

        public static bool IsProcessRunning(string processName)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                foreach (var process in processes)
                    process.Dispose();
                return processes.Length > 0;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to check if {processName} is running: {ex.Message}");
                return false;
            }
        }

        public static void SetProcessPriority(ProcessPriorityClass priority)
        {
            try
            {
                Process.GetCurrentProcess().PriorityClass = priority;
                Log.Information($"Process priority set to {priority}");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to set process priority to {priority}: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures a file is fully written and readable, especially important for network drives.
        /// Retries with delays to handle write caching and sync delays on network paths.
        /// </summary>
        public static async Task EnsureFileReady(string filePath)
        {
            // 30 seconds timeout
            const int maxRetries = 150;
            const int delayMs = 200;

            Log.Information($"Verifying file ready: {filePath}");
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (fs.Length > 0)
                        {
                            byte[] buffer = new byte[1024];
                            int bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length);
                            if (bytesRead > 0)
                            {
                                Log.Information($"File verified ready: {filePath} ({fs.Length} bytes)");
                                return;
                            }
                        }
                    }
                }
                catch (IOException ex)
                {
                    Log.Warning($"File not ready yet (attempt {i + 1}/{maxRetries}): {ex.Message}");
                }

                await Task.Delay(delayMs);
            }

            Log.Warning($"File may not be fully synced after {maxRetries} attempts: {filePath}");
        }

        private static int FindMatchingBracket(string text, int startIndex)
        {
            var openChar = text[startIndex];
            var closeChar = openChar == '{' ? '}' : ']';
            var depth = 1;
            var inString = false;
            var escaped = false;

            for (int i = startIndex + 1; i < text.Length; i++)
            {
                var c = text[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString)
                {
                    if (c == openChar)
                        depth++;
                    else if (c == closeChar)
                    {
                        depth--;
                        if (depth == 0)
                            return i;
                    }
                }
            }

            return -1; // No matching bracket found
        }
    }
}
