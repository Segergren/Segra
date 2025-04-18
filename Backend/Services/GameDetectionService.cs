﻿using Segra.Backend.Utils;
using Segra.Models;
using Serilog;
using System.Diagnostics;
using System.Management;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace Segra.Backend.Services
{
    public static class GameDetectionService
    {
        private static ManagementEventWatcher processStartWatcher;
        private static ManagementEventWatcher processStopWatcher;
        private static readonly Dictionary<string, string> deviceToDrive = new();
        private static bool _running;
        private static Task _task;
        private static CancellationTokenSource _cts;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("psapi.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern bool GetProcessImageFileName(IntPtr hprocess, StringBuilder lpExeName, out int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        [SuppressUnmanagedCodeSecurity]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime
        );

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags
        );

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        public static void StartAsync()
        {
            if (_running)
                return;

            _running = true;
            _cts = new CancellationTokenSource();

            _task = Task.Run(() =>
            {
                try
                {
                    Start();
                }
                catch (Exception ex)
                {
                    Log.Error($"GameDetectionService background task failed: {ex.Message}");
                }
            });
        }

        public static void StopAsync()
        {
            if (!_running)
                return;

            _cts?.Cancel();

            Stop();

            _running = false;
        }

        private static void Start()
        {
            Log.Information("Starting process monitoring...");
            InitializeDriveMappings();

            processStartWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance isa \"Win32_Process\""));
            processStartWatcher.EventArrived += OnProcessStarted;
            processStartWatcher.Start();

            processStopWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 1 WHERE TargetInstance isa \"Win32_Process\""));
            processStopWatcher.EventArrived += OnProcessStopped;
            processStopWatcher.Start();

            Log.Information("WMI watchers are now active.");
        }

        private static void Stop()
        {
            try
            {
                processStartWatcher?.Stop();
                processStopWatcher?.Stop();
                processStartWatcher?.Dispose();
                processStopWatcher?.Dispose();
            }
            catch { }

            Log.Information("Process monitoring stopped.");
        }

        private static void OnProcessStarted(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var processObj = e.NewEvent["TargetInstance"] as ManagementBaseObject;
                if (processObj == null) return;

                int pid = Convert.ToInt32(processObj["Handle"]);
                string exePath = ResolveProcessPath(pid);

                Log.Information($"[OnProcessStarted] Application started: PID {pid}, Path: {exePath}");
                if (IsGameExecutable(exePath))
                {
                    StartGameRecording(pid, exePath);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[OnProcessStarted] Exception: {ex.Message}");
            }
        }

        private static void OnProcessStopped(object sender, EventArrivedEventArgs e)
        {
            if (OBSUtils.CurrentTrackedFileName == null) return;

            try
            {
                var processObj = e.NewEvent["TargetInstance"] as ManagementBaseObject;
                if (processObj == null) return;

                int pid = Convert.ToInt32(processObj["Handle"]);
                string exePath = ResolveProcessPath(pid);
                string fileNameWithExtension = Path.GetFileName(exePath);

                Log.Information($"[OnProcessStopped] Application stopped: PID {pid}, Path: {exePath}");
                if (fileNameWithExtension == OBSUtils.CurrentTrackedFileName)
                {
                    Log.Information($"[OnTrackedProcessExited] Confirmed that PID {pid} is no longer running. Stopping recording.");
                    OBSUtils.StopRecording();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[OnProcessStopped] Exception: {ex.Message}");
            }
        }

        private static void StartGameRecording(int pid, string exePath)
        {
            if (Settings.Instance.State.Recording != null || OBSUtils.CurrentTrackedFileName != null)
            {
                Log.Information("[StartGameRecording] Recording already in progress. Skipping...");
                return;
            }

            Log.Information($"[StartGameRecording] Starting recording for game: PID {pid}, Path: {exePath}");

            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    OBSUtils.CurrentTrackedFileName = Path.GetFileName(exePath);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[StartGameRecording] Error accessing process {pid}: {ex.Message}");
            }

            OBSUtils.StartRecording(ExtractGameName(exePath));
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private static void InitializeDriveMappings()
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                var driveLetter = drive.Name.TrimEnd('\\');
                var sb = new StringBuilder(260);
                if (QueryDosDevice(driveLetter, sb, sb.Capacity))
                {
                    string devicePath = sb.ToString();
                    if (!deviceToDrive.ContainsKey(devicePath))
                        deviceToDrive.Add(devicePath, driveLetter);
                }
            }
        }

        private static bool IsGameExecutable(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            return exePath.Replace("\\", "/").Contains("/steamapps/common/", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveProcessPath(int pid)
        {
            if (pid <= 0) return string.Empty;
            try
            {
                var proc = Process.GetProcessById(pid);
                return Path.GetFullPath(proc.MainModule.FileName);
            }
            catch
            {
                return ResolvePathViaWinAPI(pid);
            }
        }

        private static string ResolvePathViaWinAPI(int pid)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = OpenProcess(0x00000400 | 0x00000010, false, pid);
                if (hProcess == IntPtr.Zero) return string.Empty;

                var sb = new StringBuilder(1024);
                if (!GetProcessImageFileName(hProcess, sb, out int _)) return string.Empty;
                return DevicePathToDrivePath(sb.ToString());
            }
            finally
            {
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            }
        }

        private static string DevicePathToDrivePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            foreach (var kv in deviceToDrive)
                if (path.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                    return path.Replace(kv.Key, kv.Value);
            return path;
        }

        private static string ExtractGameName(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return "Unknown";
            string steamName = AttemptSteamAcfLookup(exePath);
            if (!string.IsNullOrEmpty(steamName)) return steamName;
            return Path.GetFileNameWithoutExtension(exePath);
        }

        private static string AttemptSteamAcfLookup(string exeFilePath)
        {
            try
            {
                string normalized = exeFilePath.Replace("\\", "/");
                var splitAroundCommon = Regex.Split(normalized, "/steamapps/common/", RegexOptions.IgnoreCase);
                if (splitAroundCommon.Length < 2) return null;

                string folder = splitAroundCommon[1].Split('/')[0];
                string prefix = splitAroundCommon[0].TrimEnd('/', '\\');
                if (string.IsNullOrEmpty(prefix)) return null;

                string steamAppsDir = prefix + "/steamapps";
                if (!Directory.Exists(steamAppsDir)) return null;

                foreach (string acfFile in Directory.GetFiles(steamAppsDir, "*.acf"))
                {
                    string contents = File.ReadAllText(acfFile);
                    string acfDir = ExtractAcfField(contents, "installdir");
                    string acfName = ExtractAcfField(contents, "name");
                    if (acfDir.Equals(folder, System.StringComparison.OrdinalIgnoreCase)) return acfName;
                }
                return null;
            }
            catch { return null; }
        }

        private static string ExtractAcfField(string acfContent, string key)
        {
            if (string.IsNullOrEmpty(acfContent) || string.IsNullOrEmpty(key)) return string.Empty;
            string pattern = $"\"{key}\"\\s+\"([^\"]+)\"";
            var match = Regex.Match(acfContent, pattern, RegexOptions.IgnoreCase);
            return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : string.Empty;
        }

        // Get foreground updates
        public static class ForegroundHook
        {
            private const uint EVENT_SYSTEM_FOREGROUND = 3;
            private const uint WINEVENT_OUTOFCONTEXT = 0;
            private const int WM_QUIT = 0x0012;  // standard Windows "quit" message

            private static IntPtr _hookHandle = IntPtr.Zero;
            private static WinEventDelegate _winEventProc;

            // We store the thread and its ID so we can signal it to stop:
            private static Thread _hookThread;
            private static int _hookThreadId;

            // The callback signature
            private delegate void WinEventDelegate(
                IntPtr hWinEventHook,
                uint eventType,
                IntPtr hwnd,
                int idObject,
                int idChild,
                uint dwEventThread,
                uint dwmsEventTime);

            [DllImport("user32.dll")]
            private static extern IntPtr SetWinEventHook(
                uint eventMin,
                uint eventMax,
                IntPtr hmodWinEventProc,
                WinEventDelegate lpfnWinEventProc,
                uint idProcess,
                uint idThread,
                uint dwFlags
            );

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

            [StructLayout(LayoutKind.Sequential)]
            private struct MSG
            {
                public IntPtr hwnd;
                public uint message;
                public IntPtr wParam;
                public IntPtr lParam;
                public uint time;
                public int pt_x;
                public int pt_y;
            }

            [DllImport("user32.dll")]
            private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

            [DllImport("user32.dll")]
            private static extern bool TranslateMessage([In] ref MSG lpMsg);

            [DllImport("user32.dll")]
            private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

            [DllImport("kernel32.dll")]
            private static extern int GetCurrentThreadId();

            // We need PostThreadMessage to send WM_QUIT to the hooking thread
            [DllImport("user32.dll")]
            private static extern bool PostThreadMessage(int idThread, int msg, IntPtr wParam, IntPtr lParam);

            public static void Start()
            {
                _hookThread = new Thread(HookThreadEntry)
                {
                    IsBackground = true
                };

                _hookThread.SetApartmentState(ApartmentState.STA);
                _hookThread.Start();
            }

            public static void Stop()
            {
                if (_hookThreadId != 0)
                {
                    PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
            }

            private static void HookThreadEntry()
            {
                _hookThreadId = GetCurrentThreadId();

                _winEventProc = WinEventCallback;
                _hookHandle = SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND,
                    EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero,
                    _winEventProc,
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT
                );

                RunMessageLoop();

                if (_hookHandle != IntPtr.Zero)
                {
                    UnhookWinEvent(_hookHandle);
                    _hookHandle = IntPtr.Zero;
                }
                _hookThreadId = 0;
            }

            private static void RunMessageLoop()
            {
                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }

            private static void WinEventCallback(
                IntPtr hWinEventHook,
                uint eventType,
                IntPtr hwnd,
                int idObject,
                int idChild,
                uint dwEventThread,
                uint dwmsEventTime)
            {
                if (eventType == EVENT_SYSTEM_FOREGROUND)
                {
                    Log.Information($"Foreground window changed. New hwnd: 0x{hwnd.ToInt64():X}");

                    if (Settings.Instance.State.Recording != null) return;

                    uint pid = 0;
                    GetWindowThreadProcessId(hwnd, out pid);

                    if (pid > 0)
                    {
                        try
                        {
                            string exePath = ResolveProcessPath((int)pid);
                            if (IsGameExecutable(exePath))
                            {
                                StartGameRecording((int)pid, exePath);
                            }
                        }
                        catch (ArgumentException ex)
                        {
                            Log.Error(ex, $"Process with PID {pid} no longer exists.");
                        }
                        catch (InvalidOperationException ex)
                        {
                            Log.Error(ex, $"Failed to access process with PID {pid}.");
                        }
                    }
                    else
                    {
                        Log.Warning("No valid process associated with the window handle.");
                    }
                }
            }
        }
    }
}
