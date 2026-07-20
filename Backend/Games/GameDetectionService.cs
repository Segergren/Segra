using Serilog;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Segra.Backend.Shared;
using Segra.Backend.Recorder;
using Segra.Backend.Core.Models;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
#if WINDOWS
using System.Security;
using System.Management;
#endif

namespace Segra.Backend.Games
{
    public static class GameDetectionService
    {
        public static bool PreventRetryRecording { get; set; } = false;
#if WINDOWS
        private static ManagementEventWatcher? processStartWatcher;
        private static ManagementEventWatcher? processStopWatcher;
#endif
        private static readonly Dictionary<string, string> deviceToDrive = new();
        private static bool _running;
        private static System.Threading.Timer? _processCheckTimer;
#if !WINDOWS
        // Snapshot of PIDs seen on the previous /proc scan, to synthesize start/stop events.
        private static HashSet<int> _knownPids = new();
        private static System.Threading.Timer? _procPollTimer;
        private static int _pollInProgress;
#endif

        public static async Task StartAsync()
        {
            if (_running)
                return;

            _running = true;

            await GameUtils.InitializeAsync();

            _ = Task.Run(() =>
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

#if WINDOWS
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("psapi.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int GetProcessImageFileName(IntPtr hprocess, StringBuilder lpExeName, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [SuppressUnmanagedCodeSecurity]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
#endif

        // Paths that resolve to system locations or known non-game tooling are ignored by the watchers.
        private static bool IsIrrelevantProcessPath(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return true;
#if WINDOWS
            return exePath.StartsWith("C:/Windows/System32/")
                || exePath.StartsWith("C:/Windows/SysWOW64/")
                || exePath.StartsWith("C:/Program Files/Git/");
#else
            return exePath.StartsWith("/usr/")
                || exePath.StartsWith("/bin/")
                || exePath.StartsWith("/sbin/")
                || exePath.StartsWith("/lib/")
                || exePath.StartsWith("/lib64/")
                || exePath.StartsWith("/proc/")
                || exePath.StartsWith("/sys/");
#endif
        }

#if WINDOWS
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

            // Start periodic process scanning for games that might not trigger foreground events
            _processCheckTimer = new System.Threading.Timer(_ => CheckForGames(), null, 10000, 10000);

            Log.Information("WMI watchers and process check timer are now active.");
        }

        private static void OnProcessStarted(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var processObj = e.NewEvent["TargetInstance"] as ManagementBaseObject;
                if (processObj == null) return;

                int pid = Convert.ToInt32(processObj["Handle"]);
                string exePath = ResolveProcessPath(pid);

                HandleProcessStarted(pid, exePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[OnProcessStarted] Exception");
            }
        }
#else
        // Linux: poll /proc to synthesize process start/stop events (no WMI on Linux).
        private static void Start()
        {
            Log.Information("Starting process monitoring (Linux /proc polling)...");

            // Seed the known-PID set so we don't treat already-running processes as "started".
            _knownPids = EnumerateProcPids();

            _procPollTimer = new System.Threading.Timer(_ => PollProc(), null, 1500, 1500);
            // Also keep the recording-alive watchdog running.
            _processCheckTimer = new System.Threading.Timer(_ => CheckForGames(), null, 10000, 10000);

            Log.Information("/proc poll timer and process check timer are now active.");
        }

        private static void PollProc()
        {
            // Skip if a previous scan is still running (ShouldRecordGame does disk I/O, which can
            // outlast the timer interval); avoids two callbacks racing on _knownPids.
            if (Interlocked.Exchange(ref _pollInProgress, 1) == 1) return;
            try
            {
                var current = EnumerateProcPids();

                foreach (int pid in current)
                {
                    if (_knownPids.Contains(pid)) continue;
                    HandleProcessStarted(pid, ResolveProcessPath(pid));
                }

                if (AppState.Instance.Recording != null || AppState.Instance.PreRecording != null)
                {
                    foreach (int pid in _knownPids)
                    {
                        if (current.Contains(pid)) continue;
                        HandleProcessStopped(pid);
                    }
                }

                // Any change in the process set is a good moment to allow a retry.
                if (!current.SetEquals(_knownPids))
                    PreventRetryRecording = false;

                _knownPids = current;
            }
            catch (Exception ex)
            {
                Log.Error($"[PollProc] Exception: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _pollInProgress, 0);
            }
        }

        private static HashSet<int> EnumerateProcPids()
        {
            var pids = new HashSet<int>();
            try
            {
                foreach (var dir in Directory.EnumerateDirectories("/proc"))
                {
                    string name = Path.GetFileName(dir);
                    if (int.TryParse(name, out int pid))
                        pids.Add(pid);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to enumerate /proc: {ex.Message}");
            }
            return pids;
        }

        // Linux stop path: resolve the exe once and compare against the tracked recording.
        private static void HandleProcessStopped(int pid)
        {
            try
            {
                var recordingPid = AppState.Instance.Recording?.Pid;
                var preRecordingPid = AppState.Instance.PreRecording?.Pid;

                bool matchesRecordingPid = recordingPid.HasValue && pid == recordingPid.Value;
                bool matchesPreRecordingPid = preRecordingPid.HasValue && pid == preRecordingPid.Value;

                if (matchesRecordingPid || matchesPreRecordingPid)
                {
                    Log.Information($"[OnTrackedProcessExited] PID {pid} is no longer running. Stopping recording.");
                    _ = Task.Run(OBSService.StopRecording);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[HandleProcessStopped] Exception: {ex.Message}");
            }
        }
#endif

        // Shared start-of-process handling, called from the WMI watcher (Windows) or /proc poll (Linux).
        private static void HandleProcessStarted(int pid, string exePath)
        {
            try
            {
                if (IsIrrelevantProcessPath(exePath)) return;

                Log.Information($"[OnProcessStarted] Application started: PID {pid}, Path: {exePath}");

                OBSService.OnVoiceChatAppStarted(exePath);

                // Capture once: teardown can null PreRecording on another thread between the check and the dereference.
                var preRecording = AppState.Instance.PreRecording;
                if (preRecording != null && GameUtils.IsGameExePath(exePath))
                {
                    preRecording.Exe = exePath;
                    OBSService.UpdateGameCaptureWindow(exePath);
                }

                if (ShouldRecordGame(exePath))
                {
                    StartGameRecording(pid, exePath);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[HandleProcessStarted] Exception");
            }
        }

#if WINDOWS
        private static void OnProcessStopped(object sender, EventArrivedEventArgs e)
        {
            if (AppState.Instance.Recording == null && AppState.Instance.PreRecording == null) return;

            try
            {
                var processObj = e.NewEvent["TargetInstance"] as ManagementBaseObject;
                if (processObj == null) return;

                int pid = Convert.ToInt32(processObj["Handle"]);
                string exePath = ResolveProcessPath(pid);

                if (IsIrrelevantProcessPath(exePath)) return;

                string fileNameWithExtension = Path.GetFileName(exePath);

                Log.Information($"[OnProcessStopped] Application stopped: PID {pid}, Path: {exePath}");

                var recordingPid = AppState.Instance.Recording?.Pid;
                var preRecordingPid = AppState.Instance.PreRecording?.Pid;
                var recordingFileName = AppState.Instance.Recording?.FileName;

                bool matchesFileName = !string.IsNullOrEmpty(recordingFileName) && fileNameWithExtension == recordingFileName;
                bool matchesRecordingPid = recordingPid.HasValue && pid == recordingPid.Value;
                bool matchesPreRecordingPid = preRecordingPid.HasValue && pid == preRecordingPid.Value;

                if (matchesFileName || matchesRecordingPid || matchesPreRecordingPid)
                {
                    Log.Information($"[OnTrackedProcessExited] Confirmed that PID {pid} is no longer running. Stopping recording.");
                    _ = Task.Run(OBSService.StopRecording);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[OnProcessStopped] Exception: {ex.Message}");
            }
        }
#endif

        private static void StartGameRecording(int pid, string exePath)
        {
            if (AppState.Instance.Recording != null || AppState.Instance.PreRecording != null)
            {
                Log.Information("[StartGameRecording] Recording already in progress. Skipping...");
                return;
            }

            Log.Information($"[StartGameRecording] Starting recording for game: PID {pid}, Path: {exePath}");

            string gameName = ExtractGameName(exePath);
            string? coverImageId = GameUtils.GetCoverImageIdFromExePath(exePath);

            AppState.Instance.PreRecording = new PreRecording { Game = gameName, Status = "Waiting to start", CoverImageId = coverImageId, Pid = pid, Exe = exePath };
            OBSService.StartRecording(gameName, exePath, pid: pid);
        }

#if WINDOWS
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
#endif

        private static bool ShouldRecordGame(string exePath, string? fileDescription = null)
        {
            if (string.IsNullOrEmpty(exePath) || AppState.Instance.Recording != null || AppState.Instance.PreRecording != null) return false;

            // Normalize path for consistent comparison
            string normalizedExePath = exePath.Replace("\\", "/");

            // 1. Check the unified per-game settings list (replaces whitelist/blacklist).
            // An explicit per-game entry always wins over auto-detection: Record == true forces recording,
            // Record == false prevents it.
            var gameSetting = GameSettingsService.FindForExePath(exePath);
            if (gameSetting != null)
            {
                if (gameSetting.Record)
                {
                    Log.Information($"Game {gameSetting.Name} has custom game settings (recording enabled), will record");
                    return true;
                }

                Log.Information($"Game {gameSetting.Name} has custom game settings (recording disabled), will not record");
                return false;
            }

            // 2. Check if the game is in the games.json list
            bool isKnownGame = GameUtils.IsGameExePath(exePath);
            if (isKnownGame)
            {
                string gameName = GameUtils.GetGameNameFromExePath(exePath) ?? Path.GetFileNameWithoutExtension(exePath);
                Log.Information($"Detected known game {gameName} at {exePath}, will record");
                return true;
            }

            // 3. Check if the file path contains blacklisted text
            if (ContainsBlacklistedTextInFilePath(exePath))
            {
                return false;
            }

            // 4. Check for anticheat in file description and log window information
            if (IsAntiCheatClient(exePath, fileDescription))
            {
                Log.Information($"Detected anticheat client for this executable, will not record");
                return false;
            }

            // 5. Launcher-based detection (Steam, EA, Epic, Ubisoft, Xbox)
            string[] launcherMarkers = { "/steamapps/common/", "/EA Games/", "/Epic Games/", "/Ubisoft/", "/XboxGames/" };
            string[] launcherNames = { "Steam", "EA", "Epic", "Ubisoft", "Xbox" };

            for (int i = 0; i < launcherMarkers.Length; i++)
            {
                int markerIndex = normalizedExePath.IndexOf(launcherMarkers[i], StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0) continue;

                int afterMarker = markerIndex + launcherMarkers[i].Length;
                int nextSlash = normalizedExePath.IndexOf('/', afterMarker);
                if (nextSlash < 0) continue;

                string gameFolderName = normalizedExePath.Substring(afterMarker, nextSlash - afterMarker);
                string basePath = normalizedExePath.Substring(0, afterMarker).Replace("/", "\\");

                // Check if a known game exe exists in this folder - if so, this process is likely a splash screen and should be skipped
                // Remember that the check for if the current exe is a known game has already been done above
                if (GameUtils.HasKnownGameExeInFolder(gameFolderName, basePath))
                {
                    Log.Information($"Skipping {exePath} - a known game exe exists in '{gameFolderName}' that differs from this process");
                    return false;
                }

                Log.Information($"Detected {launcherNames[i]} game at {exePath}, will record");
                return true;
            }

            return false;
        }

        private static bool ContainsBlacklistedTextInFilePath(string exePath)
        {
            string[] blacklistedPathTexts = GameUtils.GetBlacklistedPathTexts();

            foreach (var text in blacklistedPathTexts)
            {
                if (exePath.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information($"Blocked executable with blacklisted path text '{text}': {exePath}");
                    return true;
                }
            }

            return false;
        }

        private static bool IsAntiCheatClient(string exePath, string? fileDescription = null)
        {
            try
            {
                fileDescription ??= GetFileDescription(exePath);

                if (string.IsNullOrEmpty(fileDescription))
                    return false;

                string[] blacklistedWords = GameUtils.GetBlacklistedWords();

                string? matchedWord = blacklistedWords.FirstOrDefault(word =>
                    fileDescription.Contains(word, StringComparison.OrdinalIgnoreCase));

                if (matchedWord != null)
                {
                    Log.Information($"Detected blacklisted word '{matchedWord}' in file description: '{fileDescription}' for {exePath}, will not record");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to check file description or window info: {ex.Message}");
            }

            return false;
        }

        private static string GetFileDescription(string exePath)
        {
            try
            {
                if (!File.Exists(exePath))
                    return string.Empty;

                FileVersionInfo fileInfo = FileVersionInfo.GetVersionInfo(exePath);
                string fileDescription = fileInfo.FileDescription ?? string.Empty;

#if WINDOWS
                // Fallback: if FileVersionInfo returned empty, try Shell32 API
                if (string.IsNullOrEmpty(fileDescription))
                    fileDescription = GetFileDescriptionViaShell(exePath);
#endif

                return fileDescription;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to read file description for {exePath}: {ex.Message}");
                return string.Empty;
            }
        }

        private static string ResolveProcessPath(int pid)
        {
            return PathUtils.Normalize(ResolveProcessPathRaw(pid));
        }

        private static string ResolveProcessPathRaw(int pid)
        {
            if (pid <= 0) return string.Empty;

#if !WINDOWS
            // Linux: the exe is the target of the /proc/<pid>/exe symlink.
            try
            {
                string linkPath = $"/proc/{pid}/exe";
                var fsi = new FileInfo(linkPath);
                string? target = fsi.LinkTarget;
                if (!string.IsNullOrEmpty(target))
                {
                    // LinkTarget may be relative; resolve against /proc/<pid>/cwd is unreliable, so
                    // prefer the fully-resolved target when available.
                    return fsi.ResolveLinkTarget(true)?.FullName ?? target;
                }
            }
            catch { /* process may have exited or be inaccessible */ }
            return string.Empty;
#else
            // Strategy 1: Try QueryFullProcessImageName (works for most processes including elevated)
            string path = ResolvePathViaQueryFullProcessImageName(pid);
            if (!string.IsNullOrEmpty(path)) return path;

            // Strategy 2: Try standard Process.MainModule
            try
            {
                var proc = Process.GetProcessById(pid);
                if (proc.MainModule != null)
                {
                    return Path.GetFullPath(proc.MainModule.FileName);
                }
            }
            catch { /* Continue to next strategy */ }

            // Strategy 3: Try GetProcessImageFileName (device path method)
            path = ResolvePathViaWinAPI(pid);
            if (!string.IsNullOrEmpty(path)) return path;

            // Strategy 4: Try WMI query (slower but works for elevated processes)
            path = ResolvePathViaWMI(pid);
            if (!string.IsNullOrEmpty(path)) return path;

            return string.Empty;
#endif
        }

#if WINDOWS
        private static string ResolvePathViaQueryFullProcessImageName(int pid)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                // PROCESS_QUERY_LIMITED_INFORMATION (0x1000) works even for elevated processes
                hProcess = OpenProcess(0x1000, false, pid);
                if (hProcess == IntPtr.Zero) return string.Empty;

                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                {
                    return sb.ToString();
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            }
        }

        private static string ResolvePathViaWMI(int pid)
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {pid}"))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        var path = obj["ExecutablePath"]?.ToString();
                        if (!string.IsNullOrEmpty(path))
                        {
                            return path;
                        }
                    }
                }
            }
            catch { /* WMI query failed */ }
            return string.Empty;
        }

        private static string ResolvePathViaWinAPI(int pid)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = OpenProcess(0x00000400 | 0x00000010, false, pid);
                if (hProcess == IntPtr.Zero) return string.Empty;

                var sb = new StringBuilder(1024);
                if (GetProcessImageFileName(hProcess, sb, sb.Capacity) == 0) return string.Empty;
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
#endif

        private static void CheckForGames()
        {
            try
            {
                // First, check if we're currently recording and if that process is still alive
                if (AppState.Instance.Recording != null)
                {
                    int? recordingPid = AppState.Instance.Recording.Pid;
                    if (recordingPid.HasValue && !IsProcessRunning(recordingPid.Value))
                    {
                        Log.Warning($"[ProcessCheck] Recording process PID {recordingPid} is no longer running. Stopping recording.");
                        _ = Task.Run(OBSService.StopRecording);
                        return;
                    }
                    // Process is still running, no need to check for new games
                    return;
                }

                // Skip if retry is prevented
                if (PreventRetryRecording) return;

#if WINDOWS
                IntPtr foregroundWindow = GetForegroundWindow();
                _ = GetWindowThreadProcessId(foregroundWindow, out uint foregroundPid);

                if (foregroundPid <= 0)
                {
                    Log.Debug("[ProcessCheck] No valid foreground window found");
                    return;
                }

                string foregroundExePath = ResolveProcessPath((int)foregroundPid);

                if (string.IsNullOrEmpty(foregroundExePath))
                {
                    Log.Debug("[ProcessCheck] Could not resolve foreground process path");
                    return;
                }

                if (ShouldRecordGame(foregroundExePath))
                {
                    string processName = "Unknown";
                    try
                    {
                        using var process = Process.GetProcessById((int)foregroundPid);
                        processName = process.ProcessName;
                    }
                    catch { }

                    Log.Information($"[ProcessCheck] Foreground window is a game: {processName}, Path: {foregroundExePath}");
                    StartGameRecording((int)foregroundPid, foregroundExePath);
                }
#endif
            }
            catch (Exception ex)
            {
                Log.Error($"[ProcessCheck] Error checking foreground window: {ex.Message}");
            }
        }

        private static bool IsProcessRunning(int pid)
        {
            if (pid <= 0) return false;

            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                // Process doesn't exist
                return false;
            }
            catch (InvalidOperationException)
            {
                // Process has exited
                return false;
            }
            catch
            {
                // Other errors, assume not running
                return false;
            }
        }

#if WINDOWS
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // Shell32 COM interop for file description fallback
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        private static readonly PROPERTYKEY PKEY_FileDescription = new()
        {
            fmtid = new Guid("0CEF7D53-FA64-11D1-A203-0000F81FEDEE"),
            pid = 3
        };

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        private interface IShellItem
        {
            // We only need the interface GUID for QueryInterface; no methods called directly.
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93")]
        private interface IShellItem2
        {
            // IShellItem methods (slots 0-4) - must be declared to keep vtable layout
            void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);

            // IShellItem2 methods
            void GetPropertyStore(int flags, [In] ref Guid riid, out IntPtr ppv);
            void GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, [In] ref Guid riid, out IntPtr ppv);
            void GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, [In] ref Guid riid, out IntPtr ppv);
            void GetPropertyDescriptionList(ref PROPERTYKEY keyType, [In] ref Guid riid, out IntPtr ppv);
            void Update(IntPtr pbc);
            void GetProperty(ref PROPERTYKEY key, IntPtr ppropvar);
            void GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
            void GetFileTime(ref PROPERTYKEY key, out long pft);
            void GetInt32(ref PROPERTYKEY key, out int pi);
            [PreserveSig]
            int GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
            void GetUInt32(ref PROPERTYKEY key, out uint pui);
            void GetUInt64(ref PROPERTYKEY key, out ulong pull);
            void GetBool(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.Bool)] out bool pf);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [In] string pszPath,
            IntPtr pbc,
            [In] ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem2 ppv);

        private static string GetFileDescriptionViaShell(string exePath)
        {
            IShellItem2? shellItem = null;
            try
            {
                Guid iid = typeof(IShellItem2).GUID;
                SHCreateItemFromParsingName(exePath, IntPtr.Zero, ref iid, out shellItem);

                var key = PKEY_FileDescription;
                int hr = shellItem.GetString(ref key, out string description);
                if (hr == 0 && !string.IsNullOrEmpty(description))
                {
                    return description;
                }
            }
            catch
            {
                // Shell API not available or failed
            }
            finally
            {
                if (shellItem != null)
                {
                    Marshal.ReleaseComObject(shellItem);
                }
            }

            return string.Empty;
        }
#endif

        private static string ExtractGameName(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return "Unknown";

            // First check if it's in our games.json database
            string? jsonGameName = GameUtils.GetGameNameFromExePath(exePath);
            if (!string.IsNullOrEmpty(jsonGameName)) return jsonGameName;

            // Then try Steam lookup
            string? steamName = AttemptSteamAcfLookup(exePath);
            if (!string.IsNullOrEmpty(steamName)) return steamName;

            // Then try EA Games lookup
            string? eaName = AttemptEAGamesLookup(exePath);
            if (!string.IsNullOrEmpty(eaName)) return eaName;

            // Then try Epic Games lookup
            string? epicName = AttemptEpicGamesLookup(exePath);
            if (!string.IsNullOrEmpty(epicName)) return epicName;

            // Then try Ubisoft Games lookup
            string? ubisoftName = AttemptUbisoftGamesLookup(exePath);
            if (!string.IsNullOrEmpty(ubisoftName)) return ubisoftName;

            //Then try Xbox Games lookup
            string? xboxName = AttemptXboxGamesLookup(exePath);
            if (!string.IsNullOrEmpty(xboxName)) return xboxName;

            // Fall back to filename
            return Path.GetFileNameWithoutExtension(exePath);
        }

        private static string? AttemptSteamAcfLookup(string exeFilePath)
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
                    if (acfDir.Equals(folder, StringComparison.OrdinalIgnoreCase)) return acfName;
                }
                return null;
            }
            catch { return null; }
        }

        private static string? AttemptEAGamesLookup(string exeFilePath)
        {
            try
            {
                string normalized = exeFilePath.Replace("\\", "/");
                var splitAroundEAGames = Regex.Split(normalized, "/EA Games/", RegexOptions.IgnoreCase);
                if (splitAroundEAGames.Length < 2) return null;

                // Extract the folder name after "EA Games/"
                // For example: "C:/Program Files/EA Games/EA SPORTS FC 26/game.exe" -> "EA SPORTS FC 26"
                string afterEAGames = splitAroundEAGames[1];
                string folderName = afterEAGames.Split('/')[0];

                return string.IsNullOrEmpty(folderName) ? null : folderName;
            }
            catch { return null; }
        }

        private static string? AttemptEpicGamesLookup(string exeFilePath)
        {
            try
            {
                // Epic Games manifests are stored in ProgramData
                string manifestsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "EpicGamesLauncher", "Data", "Manifests"
                );

                if (!Directory.Exists(manifestsDir)) return null;

                // Normalize the exe path for comparison
                string normalizedExePath = Path.GetFullPath(exeFilePath).Replace("\\", "/").ToLowerInvariant();

                // Scan all .item manifest files
                foreach (string manifestFile in Directory.GetFiles(manifestsDir, "*.item"))
                {
                    try
                    {
                        string jsonContent = File.ReadAllText(manifestFile);
                        using JsonDocument doc = JsonDocument.Parse(jsonContent);
                        JsonElement root = doc.RootElement;

                        // Get InstallLocation and DisplayName
                        if (root.TryGetProperty("InstallLocation", out JsonElement installLocationElement) &&
                            root.TryGetProperty("DisplayName", out JsonElement displayNameElement))
                        {
                            string? installLocation = installLocationElement.GetString();
                            string? displayName = displayNameElement.GetString();

                            if (!string.IsNullOrEmpty(installLocation) && !string.IsNullOrEmpty(displayName))
                            {
                                string normalizedInstallLocation = Path.GetFullPath(installLocation).Replace("\\", "/").ToLowerInvariant();

                                // Check if the exe path starts with the install location
                                if (normalizedExePath.StartsWith(normalizedInstallLocation))
                                {
                                    return displayName;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Skip invalid manifest files
                        continue;
                    }
                }

                return null;
            }
            catch { return null; }
        }

        private static string? AttemptUbisoftGamesLookup(string exeFilePath)
        {
            try
            {
                // Ubisoft Connect doesn't have easily accessible manifest files
                // Instead, use the File Description from the EXE metadata
                if (!File.Exists(exeFilePath)) return null;

                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(exeFilePath);
                string? fileDescription = versionInfo.FileDescription;

                if (!string.IsNullOrEmpty(fileDescription))
                {
                    return fileDescription;
                }

                return null;
            }
            catch { return null; }
        }

        private static string? AttemptXboxGamesLookup(string exeFilePath)
        {
            try
            {
                string normalized = exeFilePath.Replace("\\", "/");
                var splitAroundContent = Regex.Split(normalized, "/Content/", RegexOptions.IgnoreCase);
                if (splitAroundContent.Length < 2) return null;

                string folder = splitAroundContent[0].TrimEnd('/', '\\');
                if (string.IsNullOrEmpty(folder)) return null;

                string contentDir = folder + "/Content";
                if (!Directory.Exists(contentDir)) return null;

                string configFile = contentDir + "/MicrosoftGame.config";
                if (!File.Exists(configFile)) return null;

                XDocument config = XDocument.Load(configFile);
                var displayNameAttribute = config.Root
                    ?.Element(XName.Get("ShellVisuals", config.Root.GetDefaultNamespace().NamespaceName))
                    ?.Attribute(XName.Get("DefaultDisplayName", config.Root.GetDefaultNamespace().NamespaceName));
                if (displayNameAttribute != null) return displayNameAttribute.Value;

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

#if !WINDOWS
        // Wayland forbids querying other clients' foreground window; /proc polling handles detection.
        public static class ForegroundHook
        {
            public static void Start() { }
            public static void Stop() { }
        }
#else
        // Get foreground updates
        public static class ForegroundHook
        {
            private const uint EVENT_SYSTEM_FOREGROUND = 3;
            private const uint WINEVENT_OUTOFCONTEXT = 0;
            private const int WM_QUIT = 0x0012;  // standard Windows "quit" message

            private static IntPtr _hookHandle = IntPtr.Zero;
            private static WinEventDelegate? _winEventProc;

            // We store the thread and its ID so we can signal it to stop:
            private static Thread? _hookThread;
            private static int _hookThreadId;

            // Track the last window we logged so repeated foreground events for the same HWND don't spam the log
            private static IntPtr _lastLoggedHwnd = IntPtr.Zero;

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
                    // Reset retry recording flag to allow retrying recording if the user has changed foreground window
                    PreventRetryRecording = false;

                    if (AppState.Instance.Recording != null) return;

                    // The foreground hook can fire repeatedly for the same window; skip if it matches what we last logged
                    if (hwnd == _lastLoggedHwnd) return;

                    _ = GetWindowThreadProcessId(hwnd, out uint pid);

                    if (pid <= 0)
                    {
                        Log.Warning($"Foreground window changed. HWND: 0x{hwnd.ToInt64():X8}, no valid process associated with the window handle.");
                        _lastLoggedHwnd = hwnd;
                        return;
                    }

                    try
                    {
                        string exePath = ResolveProcessPath((int)pid);

                        // Windows shell surfaces (Explorer, Start menu, Search, etc.) own many windows, change
                        // foreground constantly, and are never recorded, so skip them to avoid log spam
                        if (string.Equals(Path.GetFileName(exePath), "explorer.exe", StringComparison.OrdinalIgnoreCase)
                            || exePath.StartsWith("C:/Windows/SystemApps/", StringComparison.OrdinalIgnoreCase))
                            return;

                        string fileDescription = GetFileDescription(exePath);

                        Log.Information($"Foreground window changed. HWND: 0x{hwnd.ToInt64():X8}, PID: {pid,5}, file description: '{fileDescription}' for {exePath}");
                        _lastLoggedHwnd = hwnd;

                        if (ShouldRecordGame(exePath, fileDescription))
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
            }
        }
#endif
    }
}
