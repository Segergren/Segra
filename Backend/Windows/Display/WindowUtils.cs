using Segra.Backend.Core.Models;
using Segra.Backend.Recorder;
using Segra.Backend.Services;
using Serilog;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Segra.Backend.Windows.Display
{
    public static class WindowUtils
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_MINIMIZE = 0x20000000;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_CHILD = 0x40000000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_APPWINDOW = 0x00040000;
        private const uint GW_OWNER = 4;

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

        public static bool GetWindowDimensionsByPreRecordingExeOrPid(out uint width, out uint height)
        {
            width = 0;
            height = 0;

            // Check for captured dimensions first - these are the most accurate and don't require window handle lookup
            if (OBSService.CapturedWindowWidth.HasValue && OBSService.CapturedWindowHeight.HasValue)
            {
                width = OBSService.CapturedWindowWidth.Value;
                height = OBSService.CapturedWindowHeight.Value;
                Log.Information($"Using captured window dimensions from OBS logs: {width}x{height}");
                return true;
            }

            const int maxAttempts = 60;
            const int delayMs = 1000;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                PreRecording? preRecording = Settings.Instance.State.PreRecording;

                if (preRecording == null)
                {
                    Log.Information("No longer pre recording, exiting GetWindowDimensions");
                    return false;
                }

                string? executableFileName = preRecording?.Exe;

                Log.Information($"Captured dimensions not available, attempting to find window for: {executableFileName}");

                // Check for captured dimensions on each attempt
                if (OBSService.CapturedWindowWidth.HasValue && OBSService.CapturedWindowHeight.HasValue)
                {
                    width = OBSService.CapturedWindowWidth.Value;
                    height = OBSService.CapturedWindowHeight.Value;
                    Log.Information($"Using captured window dimensions from OBS logs: {width}x{height}");
                    return true;
                }

                IntPtr targetWindow = TryFindWindow(executableFileName, attempt);

                if (targetWindow != IntPtr.Zero)
                {
                    return GetWindowDimensionsByWindowHandle(targetWindow, executableFileName, attempt, out width, out height);
                }

                if (attempt < maxAttempts)
                {
                    Thread.Sleep(delayMs);
                }
            }

            Log.Warning($"Could not find window for executable after {maxAttempts} seconds: {Settings.Instance.State.PreRecording?.Exe}");
            return false;
        }

        private static IntPtr TryFindWindow(string? executableFileName, int attempt)
        {
            if (string.IsNullOrEmpty(executableFileName))
            {
                if (attempt == 1) Log.Warning("TryFindWindow called with empty executable name");
                return IntPtr.Zero;
            }

            try
            {
                var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executableFileName));
                if (processes.Length == 0)
                {
                    if (attempt == 1) Log.Information($"Attempt {attempt}: No process found for {executableFileName}, will retry...");
                    return IntPtr.Zero;
                }

                if (attempt == 1) Log.Information($"Found {processes.Length} process(es) with name {executableFileName}");

                // Iterate through all processes with the same name
                foreach (var process in processes)
                {
                    IntPtr targetWindow = TryFindWindowForProcess(process, attempt);
                    if (targetWindow != IntPtr.Zero)
                    {
                        return targetWindow;
                    }
                }

                if (attempt % 5 == 0) Log.Information($"Attempt {attempt}: No valid window found in any of {processes.Length} processes, will retry...");
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                if (attempt == 1) Log.Warning($"Error finding window: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        private static IntPtr TryFindWindowForProcess(Process process, int attempt)
        {
            IntPtr targetWindow = IntPtr.Zero;
            bool verboseLog = (attempt == 1);

            // Check if process is still alive
            try
            {
                if (process.HasExited)
                {
                    if (verboseLog) Log.Information($"Process {process.Id} has exited, skipping...");
                    return IntPtr.Zero;
                }
            }
            catch
            {
                if (verboseLog) Log.Information($"Process no longer accessible, skipping...");
                return IntPtr.Zero;
            }

            uint targetProcessId = (uint)process.Id;
            if (verboseLog) Log.Information($"Checking process ID {targetProcessId}");

            // Method 1: Try Process.MainWindowHandle first
            try
            {
                var mainHandle = process.MainWindowHandle;
                if (verboseLog) Log.Information($"Method 1: Process.MainWindowHandle = {mainHandle}");
                if (mainHandle != IntPtr.Zero)
                {
                    bool isVisible = IsWindowVisible(mainHandle);
                    bool isValidGame = IsValidGameWindow(mainHandle, logReason: verboseLog);
                    if (verboseLog) Log.Information($"  IsWindowVisible={isVisible}, IsValidGameWindow={isValidGame}");

                    if (isVisible && isValidGame)
                    {
                        targetWindow = mainHandle;
                        Log.Information($"Found window via Process.MainWindowHandle for process {targetProcessId}");
                        return targetWindow;
                    }
                }
            }
            catch (Exception ex)
            {
                if (verboseLog) Log.Information($"Method 1 failed: {ex.Message}");
            }

            // First, enumerate ALL windows for this process for debugging (only on first attempt)
            if (verboseLog)
            {
                Log.Information($"Enumerating all windows for process {targetProcessId}:");
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                    if (windowProcessId == targetProcessId)
                    {
                        StringBuilder className = new StringBuilder(256);
                        GetClassName(hWnd, className, className.Capacity);
                        string classNameStr = className.ToString();

                        int titleLength = GetWindowTextLength(hWnd);
                        string title = "";
                        if (titleLength > 0)
                        {
                            StringBuilder titleBuilder = new StringBuilder(titleLength + 1);
                            GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                            title = titleBuilder.ToString();
                        }

                        bool isVisible = IsWindowVisible(hWnd);
                        GetClientRect(hWnd, out RECT rect);

                        Log.Information($"  Window {hWnd}: class='{classNameStr}', title='{title}', visible={isVisible}, size={rect.Width}x{rect.Height}");
                    }
                    return true;
                }, IntPtr.Zero);
            }

            // Method 2: Look for known game window class names (Unreal Engine, Unity, etc.)
            if (targetWindow == IntPtr.Zero)
            {
                if (verboseLog) Log.Information("Method 2: Looking for known game window classes...");
                var knownGameClasses = new[] { "UnrealWindow", "UnityWndClass", "SDL_app", "GLFW", "LaunchUnrealUWindowsClient", "CryENGINE" };
                var candidateWindows = new List<(IntPtr hWnd, string className, int area)>();

                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                    if (windowProcessId == targetProcessId && IsWindowVisible(hWnd))
                    {
                        StringBuilder className = new StringBuilder(256);
                        GetClassName(hWnd, className, className.Capacity);
                        string classNameStr = className.ToString();

                        foreach (var knownClass in knownGameClasses)
                        {
                            if (classNameStr.Contains(knownClass, StringComparison.OrdinalIgnoreCase))
                            {
                                if (GetClientRect(hWnd, out RECT rect) && rect.Width > 0 && rect.Height > 0)
                                {
                                    candidateWindows.Add((hWnd, classNameStr, rect.Width * rect.Height));
                                    if (verboseLog) Log.Information($"  Found known game window class: {classNameStr}, size={rect.Width}x{rect.Height}");
                                }
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (candidateWindows.Count > 0)
                {
                    var best = candidateWindows.OrderByDescending(w => w.area).First();
                    targetWindow = best.hWnd;
                    Log.Information($"Selected window with class {best.className} (area: {best.area}) for process {targetProcessId}");
                    return targetWindow;
                }
                else if (verboseLog)
                {
                    Log.Information("  No known game window classes found");
                }
            }

            // Method 3: Find the largest visible top-level window owned by the process
            if (targetWindow == IntPtr.Zero)
            {
                if (verboseLog) Log.Information("Method 3: Looking for largest visible top-level window...");
                var candidateWindows = new List<(IntPtr hWnd, string className, string title, int area)>();

                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                    if (windowProcessId == targetProcessId)
                    {
                        StringBuilder className = new StringBuilder(256);
                        GetClassName(hWnd, className, className.Capacity);
                        string classNameStr = className.ToString();

                        int titleLength = GetWindowTextLength(hWnd);
                        string title = "";
                        if (titleLength > 0)
                        {
                            StringBuilder titleBuilder = new StringBuilder(titleLength + 1);
                            GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                            title = titleBuilder.ToString();
                        }

                        if (!IsWindowVisible(hWnd))
                            return true;

                        if (!IsValidGameWindow(hWnd))
                            return true;

                        if (ShouldSkipWindowClass(classNameStr))
                            return true;

                        if (GetClientRect(hWnd, out RECT rect) && rect.Width > 100 && rect.Height > 100)
                        {
                            candidateWindows.Add((hWnd, classNameStr, title, rect.Width * rect.Height));
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (candidateWindows.Count > 0)
                {
                    var sorted = candidateWindows.OrderByDescending(w => w.area).ToList();
                    var best = sorted.First();
                    targetWindow = best.hWnd;
                    Log.Information($"Selected largest window: class='{best.className}', title='{best.title}', area={best.area} for process {targetProcessId}");
                    return targetWindow;
                }
                else if (verboseLog)
                {
                    Log.Information("  No valid candidate windows found");
                }
            }

            return IntPtr.Zero;
        }

        private static bool IsValidGameWindow(IntPtr hWnd, bool logReason = false)
        {
            int style = GetWindowLong(hWnd, GWL_STYLE);
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);

            if (logReason)
            {
                Log.Information($"  Window {hWnd}: style=0x{style:X8}, exStyle=0x{exStyle:X8}");
            }

            // Must be visible
            if ((style & (int)WS_VISIBLE) == 0)
            {
                if (logReason) Log.Information($"  Rejected: not visible");
                return false;
            }

            // Must not be minimized
            if ((style & (int)WS_MINIMIZE) != 0)
            {
                if (logReason) Log.Information($"  Rejected: minimized");
                return false;
            }

            // Must not be a child window
            if ((style & (int)WS_CHILD) != 0)
            {
                if (logReason) Log.Information($"  Rejected: child window");
                return false;
            }

            // Skip tool windows unless they're also app windows
            if ((exStyle & (int)WS_EX_TOOLWINDOW) != 0 && (exStyle & (int)WS_EX_APPWINDOW) == 0)
            {
                if (logReason) Log.Information($"  Rejected: tool window without app window style");
                return false;
            }

            // Must not have an owner (top-level window)
            var owner = GetWindow(hWnd, GW_OWNER);
            if (owner != IntPtr.Zero)
            {
                if (logReason) Log.Information($"  Rejected: has owner {owner}");
                return false;
            }

            return true;
        }

        private static bool ShouldSkipWindowClass(string className)
        {
            var skipClasses = new[]
            {
                "IME", "MSCTFIME", "tooltips_class", "SysShadow", "EdgeUiInputTopWndClass",
                "Shell_TrayWnd", "WorkerW", "Progman", "NotifyIconOverflowWindow",
                "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow"
            };

            foreach (var skip in skipClasses)
            {
                if (className.Contains(skip, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
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

        private static bool GetWindowDimensionsByWindowHandle(IntPtr windowHandle, string? executableFileName, int windowHandleAttempts, out uint width, out uint height)
        {
            width = 0;
            height = 0;
            int maxStableWindowDimensionsAttempts = 60;
            int stableWindowDimensionsAttempt = 0;
            uint? lastWidth = null;
            uint? lastHeight = null;
            int stabilityChecks = 0;
            int requiredStabilityChecks = 0;

            while (stableWindowDimensionsAttempt < maxStableWindowDimensionsAttempts)
            {
                stableWindowDimensionsAttempt += 1;

                // Check if OBS captured dimensions are available and match display size
                if (OBSService.CapturedWindowWidth.HasValue && OBSService.CapturedWindowHeight.HasValue)
                {
                    uint capturedWidth = OBSService.CapturedWindowWidth.Value;
                    uint capturedHeight = OBSService.CapturedWindowHeight.Value;

                    SettingsService.GetPrimaryMonitorResolution(out uint displayWidth, out uint displayHeight);

                    if (capturedWidth == displayWidth && capturedHeight == displayHeight)
                    {
                        width = capturedWidth;
                        height = capturedHeight;
                        Log.Information($"Using captured window dimensions from OBS logs (matches display size): {width}x{height}");
                        return true;
                    }
                }

                if (!GetClientRect(windowHandle, out RECT rect))
                {
                    Log.Warning($"Failed to get client rect for window handle {windowHandle}");

                    // Sometimes the window handle becomes invalid, so we need to find it again
                    IntPtr targetWindow = TryFindWindow(executableFileName, windowHandleAttempts);
                    if (targetWindow != IntPtr.Zero)
                    {
                        windowHandle = targetWindow;
                    }
                    else if (stableWindowDimensionsAttempt >= maxStableWindowDimensionsAttempts)
                    {
                        Log.Warning($"Failed to find window for executable {executableFileName} after {maxStableWindowDimensionsAttempts} attempts");
                        return false;
                    }
                    Thread.Sleep(1000);
                    continue;
                }

                // GetClientRect returns physical pixel dimensions because Segra is
                // PerMonitorV2 DPI-aware (declared in app.manifest). No DPI conversion needed.
                width = (uint)rect.Width;
                height = (uint)rect.Height;

                // If this is the first attempt and we have a standard aspect ratio, return immediately
                // This prevents unnecessary waiting for games that are already open
                bool isStandardAspectRatio = IsStandardAspectRatio(width, height);
                if (isStandardAspectRatio && stableWindowDimensionsAttempt == 1 && windowHandleAttempts == 1)
                {
                    return true;
                }

                // Check if dimensions match a display (fullscreen) - needs fewer stability checks
                SettingsService.GetPrimaryMonitorResolution(out uint monitorWidth, out uint monitorHeight);
                bool isFullscreen = width == monitorWidth && height == monitorHeight;

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

                            requiredStabilityChecks = isFullscreen ? 5 : isStandardAspectRatio ? 10 : 30;
                            stabilityChecks = 0;

                            Thread.Sleep(1000);
                        }
                    }
                    else
                    {
                        // First valid dimensions detected
                        requiredStabilityChecks = isFullscreen ? 5 : isStandardAspectRatio ? 10 : 30;
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
                    if (stableWindowDimensionsAttempt == 1)
                    {
                        Log.Information($"Window dimensions are {width}x{height}, waiting for valid size...");
                    }
                    Thread.Sleep(1000);
                }
            }

            Log.Warning($"Window dimension timeout after {maxStableWindowDimensionsAttempts} seconds");
            return false;
        }
    }
}
