using Segra.Backend.Core.Models;
using Segra.Backend.Games;
using Segra.Backend.Recorder;
using Segra.Backend.Utils;
using Serilog;
using System.Diagnostics;
using System.Management;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Segra.Backend.Services
{
    public static class GameDetectionService
    {
        public static bool PreventRetryRecording { get; set; } = false;
        private static ManagementEventWatcher? processStartWatcher;
        private static ManagementEventWatcher? processStopWatcher;
        private static readonly Dictionary<string, string> deviceToDrive = new();
        private static bool _running;
        private static CancellationTokenSource? _cts;
        private static System.Threading.Timer? _processCheckTimer;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("psapi.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern bool GetProcessImageFileName(IntPtr hprocess, StringBuilder lpExeName, out int size);

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

        public static async void StartAsync()
        {
            if (_running)
                return;

            _running = true;
            _cts = new CancellationTokenSource();

            // Initialize game detection from JSON
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

                // We can't resolve the path or it's irrelevant, so it's most likely not a game
                if (string.IsNullOrEmpty(exePath) || exePath.StartsWith("C:\\Windows\\System32\\") || exePath.StartsWith("C:\\Windows\\SysWOW64\\") || exePath.StartsWith("C:\\Program Files\\Git\\")) return;

                Log.Information($"[OnProcessStarted] Application started: PID {pid}, Path: {exePath}");
                if (ShouldRecordGame(exePath))
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
            if (Settings.Instance.State.Recording == null && Settings.Instance.State.PreRecording == null) return;

            try
            {
                var processObj = e.NewEvent["TargetInstance"] as ManagementBaseObject;
                if (processObj == null) return;

                int pid = Convert.ToInt32(processObj["Handle"]);
                string exePath = ResolveProcessPath(pid);
                string fileNameWithExtension = Path.GetFileName(exePath);

                // We can't resolve the path or it's irrelevant, so it's most likely not a game
                if (string.IsNullOrEmpty(exePath) || exePath.StartsWith("C:\\Windows\\System32\\") || exePath.StartsWith("C:\\Windows\\SysWOW64\\") || exePath.StartsWith("C:\\Program Files\\Git\\")) return;

                Log.Information($"[OnProcessStopped] Application stopped: PID {pid}, Path: {exePath}");

                var recordingPid = Settings.Instance.State.Recording?.Pid;
                var preRecordingPid = Settings.Instance.State.PreRecording?.Pid;
                var recordingFileName = Settings.Instance.State.Recording?.FileName;

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

        private static void StartGameRecording(int pid, string exePath)
        {
            if (Settings.Instance.State.Recording != null || Settings.Instance.State.PreRecording != null)
            {
                Log.Information("[StartGameRecording] Recording already in progress. Skipping...");
                return;
            }

            Log.Information($"[StartGameRecording] Starting recording for game: PID {pid}, Path: {exePath}");
            OBSService.StartRecording(ExtractGameName(exePath), exePath, pid: pid);
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

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

        private static bool ShouldRecordGame(string exePath, bool isPeriodicCheck = false)
        {
            if (string.IsNullOrEmpty(exePath) || Settings.Instance.State.Recording != null || Settings.Instance.State.PreRecording != null) return false;

            // Normalize path for consistent comparison
            string normalizedExePath = exePath.Replace("\\", "/");

            // 1. Check if the game is in the whitelist - if so, always record
            var whitelist = Settings.Instance.Whitelist;
            foreach (var game in whitelist)
            {
                if (game.Paths.Any(path => normalizedExePath.EndsWith(path.Replace("\\", "/"), StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Information($"Game {game.Name} found in whitelist, will record");
                    return true;
                }
            }

            // 2. Check if the game is in the blacklist - if so, never record
            var blacklist = Settings.Instance.Blacklist;
            foreach (var game in blacklist)
            {
                if (game.Paths.Any(path => normalizedExePath.EndsWith(path.Replace("\\", "/"), StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Information($"Game {game.Name} found in blacklist, will not record");
                    return false;
                }
            }

            // 3. Check if the file path contains blacklisted text
            if (ContainsBlacklistedTextInFilePath(exePath))
            {
                return false;
            }

            // 4. Check if the game is in the games.json list
            bool isKnownGame = GameUtils.IsGameExePath(exePath);
            if (isKnownGame)
            {
                string gameName = GameUtils.GetGameNameFromExePath(exePath) ?? Path.GetFileNameWithoutExtension(exePath);
                Log.Information($"Detected known game {gameName} at {exePath}, will record");
                return true;
            }

            // 5. Check for anticheat in file description and log window information
            if (IsAntiCheatClient(exePath, isPeriodicCheck))
            {
                Log.Information($"Detected anticheat client for this executable, will not record");
                return false;
            }

            // 6. If not in any list, use the default Steam detection
            bool isSteamGame = exePath.Replace("\\", "/").Contains("/steamapps/common/", StringComparison.OrdinalIgnoreCase);
            if (isSteamGame)
            {
                Log.Information($"Detected Steam game at {exePath}, will record");
            }

            // 7. If not in any list, use EA Games detection
            bool isEAGame = exePath.Replace("\\", "/").Contains("/EA Games/", StringComparison.OrdinalIgnoreCase);
            if (isEAGame)
            {
                Log.Information($"Detected EA game at {exePath}, will record");
            }

            // 8. If not in any list, use Epic Games detection
            bool isEpicGame = exePath.Replace("\\", "/").Contains("/Epic Games/", StringComparison.OrdinalIgnoreCase);
            if (isEpicGame)
            {
                Log.Information($"Detected Epic game at {exePath}, will record");
            }

            // 9. If not in any list, use Ubisoft Games detection
            bool isUbisoftGame = exePath.Replace("\\", "/").Contains("/Ubisoft/", StringComparison.OrdinalIgnoreCase);
            if (isUbisoftGame)
            {
                Log.Information($"Detected Ubisoft game at {exePath}, will record");
            }

            return isSteamGame || isEAGame || isEpicGame || isUbisoftGame;
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

        private static bool IsAntiCheatClient(string exePath, bool isPeriodicCheck)
        {
            try
            {
                string fileDescription = string.Empty;
                string windowTitle = string.Empty;
                string windowClass = string.Empty;

                // Get blacklisted words from loaded blacklist
                string[] blacklistedWords = GameUtils.GetBlacklistedWords();

                if (File.Exists(exePath))
                {
                    FileVersionInfo fileInfo = FileVersionInfo.GetVersionInfo(exePath);
                    fileDescription = fileInfo.FileDescription ?? string.Empty;

                    // Fallback: if FileVersionInfo returned empty, try Shell32 API
                    if (string.IsNullOrEmpty(fileDescription))
                    {
                        fileDescription = GetFileDescriptionViaShell(exePath);
                    }

                    // Prevent logging file description for periodic checks (to avoid spamming and for security concerns)
                    if (!isPeriodicCheck)
                        Log.Information($"File description: '{fileDescription}' for {exePath}");

                    // Check for blacklisted words in file description
                    if (!string.IsNullOrEmpty(fileDescription))
                    {
                        string? matchedWord = blacklistedWords.FirstOrDefault(word =>
                            fileDescription.Contains(word, StringComparison.OrdinalIgnoreCase));

                        if (matchedWord != null)
                        {
                            Log.Information($"Detected blacklisted word '{matchedWord}' in file description: '{fileDescription}' for {exePath}, will not record");
                            return true;
                        }
                    }
                }

                // Log window information etc for improved detection in the future
                try
                {
                    Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath));
                    if (processes.Length > 0)
                    {
                        IntPtr mainWindowHandle = processes[0].MainWindowHandle;
                        if (mainWindowHandle != IntPtr.Zero)
                        {
                            StringBuilder titleBuilder = new StringBuilder(256);
                            if (GetWindowText(mainWindowHandle, titleBuilder, titleBuilder.Capacity) > 0)
                            {
                                windowTitle = titleBuilder.ToString();
                            }

                            StringBuilder classBuilder = new StringBuilder(256);
                            if (GetClassName(mainWindowHandle, classBuilder, classBuilder.Capacity) > 0)
                            {
                                windowClass = classBuilder.ToString();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to get window information: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to check file description or window info: {ex.Message}");
            }

            return false;
        }

        private static string ResolveProcessPath(int pid)
        {
            if (pid <= 0) return string.Empty;

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
        }

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

        private static void CheckForGames()
        {
            try
            {
                // First, check if we're currently recording and if that process is still alive
                if (Settings.Instance.State.Recording != null)
                {
                    int? recordingPid = Settings.Instance.State.Recording.Pid;
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

                // Get the foreground window and its process ID
                IntPtr foregroundWindow = GetForegroundWindow();
                _ = GetWindowThreadProcessId(foregroundWindow, out uint foregroundPid);

                if (foregroundPid <= 0)
                {
                    Log.Debug("[ProcessCheck] No valid foreground window found");
                    return;
                }

                // Get the executable path of the foreground process
                string foregroundExePath = ResolveProcessPath((int)foregroundPid);

                if (string.IsNullOrEmpty(foregroundExePath))
                {
                    Log.Debug("[ProcessCheck] Could not resolve foreground process path");
                    return;
                }

                // Check if the foreground process is a game we should record
                if (ShouldRecordGame(foregroundExePath, true))
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // Shell32 COM interop for file description fallback
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        private static readonly PROPERTYKEY PKEY_FileDescription = new PROPERTYKEY
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
            private static WinEventDelegate? _winEventProc;

            // We store the thread and its ID so we can signal it to stop:
            private static Thread? _hookThread;
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

                    // Reset retry recording flag to allow retrying recording if the user has changed foreground window
                    PreventRetryRecording = false;

                    if (Settings.Instance.State.Recording != null) return;

                    _ = GetWindowThreadProcessId(hwnd, out uint pid);

                    Log.Information($"Foreground window process ID: {pid}");

                    if (pid > 0)
                    {
                        try
                        {
                            string exePath = ResolveProcessPath((int)pid);
                            if (ShouldRecordGame(exePath))
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
