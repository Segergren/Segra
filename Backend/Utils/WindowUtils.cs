using Serilog;
using System.Runtime.InteropServices;
using System.Text;

namespace Segra.Backend.Utils
{
    public static class WindowUtils
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        public static bool GetWindowDimensions(string? executableFileName, out uint width, out uint height)
        {
            width = 0;
            height = 0;

            // Check for captured dimensions first - these are the most accurate and don't require window handle lookup
            if (OBSUtils.CapturedWindowWidth.HasValue && OBSUtils.CapturedWindowHeight.HasValue)
            {
                width = OBSUtils.CapturedWindowWidth.Value;
                height = OBSUtils.CapturedWindowHeight.Value;
                Log.Information($"Using captured window dimensions from OBS logs: {width}x{height}");
                return true;
            }

            if (string.IsNullOrEmpty(executableFileName))
            {
                return false;
            }

            Log.Information($"Captured dimensions not available, attempting to find window for: {executableFileName}");

            IntPtr targetWindow = IntPtr.Zero;

            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executableFileName));
                if (processes.Length > 0)
                {
                    uint targetProcessId = (uint)processes[0].Id;
                    Log.Information($"Found process ID {targetProcessId} for {executableFileName}");

                    EnumWindows((hWnd, lParam) =>
                    {
                        GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                        if (windowProcessId == targetProcessId)
                        {
                            StringBuilder className = new StringBuilder(256);
                            GetClassName(hWnd, className, className.Capacity);

                            string classNameStr = className.ToString();
                            if (!string.IsNullOrEmpty(classNameStr) &&
                                !classNameStr.Contains("IME") &&
                                !classNameStr.Contains("MSCTFIME"))
                            {
                                targetWindow = hWnd;
                                return false;
                            }
                        }
                        return true;
                    }, IntPtr.Zero);

                    foreach (var process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to find process for {executableFileName}: {ex.Message}");
                return false;
            }

            if (targetWindow == IntPtr.Zero)
            {
                Log.Warning($"Could not find window for executable: {executableFileName}");
                return false;
            }

            return GetWindowDimensions(targetWindow, out width, out height);
        }

        private static bool IsStandardAspectRatio(uint width, uint height)
        {
            if (width == 0 || height == 0) return false;

            // Calculate GCD to reduce the aspect ratio to its simplest form
            uint gcd = GCD(width, height);
            uint aspectWidth = width / gcd;
            uint aspectHeight = height / gcd;

            // Check against standard aspect ratios
            var standardRatios = new (uint w, uint h)[]
            {
                (32, 9),   // 32:9
                (21, 9),   // 21:9
                (16, 9),   // 16:9
                (16, 10),  // 16:10
                (3, 2),    // 3:2
                (4, 3),    // 4:3
                (5, 4)     // 5:4
            };

            foreach (var (w, h) in standardRatios)
            {
                if (aspectWidth == w && aspectHeight == h)
                {
                    return true;
                }
            }

            return false;
        }

        private static uint GCD(uint a, uint b)
        {
            while (b != 0)
            {
                uint temp = b;
                b = a % b;
                a = temp;
            }
            return a;
        }

        private static bool GetWindowDimensions(IntPtr windowHandle, out uint width, out uint height)
        {
            width = 0;
            height = 0;
            int maxAttempts = 20;
            int attempts = 0;
            uint? lastWidth = null;
            uint? lastHeight = null;
            int stabilityChecks = 0;
            int requiredStabilityChecks = 0;

            while (attempts < maxAttempts)
            {
                // If OBS captured dimensions are available, use them
                if (OBSUtils.CapturedWindowWidth.HasValue && OBSUtils.CapturedWindowHeight.HasValue)
                {
                    width = OBSUtils.CapturedWindowWidth.Value;
                    height = OBSUtils.CapturedWindowHeight.Value;
                    Log.Information($"Using captured window dimensions from OBS logs: {width}x{height}");
                    return true;
                }

                if (!GetWindowRect(windowHandle, out RECT rect))
                {
                    Log.Warning($"Failed to get window rect for window handle {windowHandle}");
                    return false;
                }

                width = (uint)rect.Width;
                height = (uint)rect.Height;

                // Window dimensions are 0x0 or 1x1 when the window is not visible
                if (width > 1 && height > 1)
                {
                    if (lastWidth.HasValue && lastHeight.HasValue)
                    {
                        if (lastWidth.Value == width && lastHeight.Value == height)
                        {
                            // Dimensions are stable, increment stability counter
                            stabilityChecks++;
                            
                            if (stabilityChecks >= requiredStabilityChecks)
                            {
                                Log.Information($"Retrieved stable window dimensions: {width}x{height} after {stabilityChecks} checks");
                                return true;
                            }
                            
                            Log.Information($"Window dimensions stable at {width}x{height}, check {stabilityChecks}/{requiredStabilityChecks}");
                            Thread.Sleep(1000);
                        }
                        else
                        {
                            // Dimensions changed, reset stability counter and recalculate required checks
                            Log.Information($"Window dimensions changed from {lastWidth}x{lastHeight} to {width}x{height}, resetting stability timer...");
                            lastWidth = width;
                            lastHeight = height;
                            
                            bool isStandardAspectRatio = IsStandardAspectRatio(width, height);
                            requiredStabilityChecks = isStandardAspectRatio ? 5 : 20;
                            stabilityChecks = 0;
                            
                            Thread.Sleep(1000);
                        }
                    }
                    else
                    {
                        // First valid dimensions detected
                        bool isStandardAspectRatio = IsStandardAspectRatio(width, height);
                        requiredStabilityChecks = isStandardAspectRatio ? 5 : 20;
                        stabilityChecks = 0;
                        
                        string aspectRatioNote = isStandardAspectRatio ? "standard aspect ratio" : "non-standard aspect ratio";
                        Log.Information($"Window dimensions are {width}x{height} ({aspectRatioNote}), waiting {requiredStabilityChecks} seconds to verify stability...");
                        
                        lastWidth = width;
                        lastHeight = height;
                        Thread.Sleep(1000);
                    }
                }
                else
                {
                    if (attempts == 0)
                    {
                        Log.Information($"Window dimensions are {width}x{height}, waiting for valid size...");
                    }
                    Thread.Sleep(1000);
                }

                attempts++;
            }

            Log.Warning($"Window dimension timeout after {maxAttempts} seconds");
            return false;
        }
    }
}
