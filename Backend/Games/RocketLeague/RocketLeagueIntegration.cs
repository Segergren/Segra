using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Serilog;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Segra.Backend.Games.RocketLeague
{
    /// <summary>
    /// How it works:
    /// 1. Uses pattern scanning to find GObjects/GNames arrays (adapts to ASLR)
    /// 2. Scans GObjects for GFxData_PRI_TA instances (UI player data with names)
    /// 3. Identifies local player by name frequency (local player appears most often)
    /// 4. Links GFxData_PRI_TA to PRI_TA (live stats) via offset 0x138
    /// 5. Monitors PRI_TA.Goals every 50ms and creates bookmarks on increment
    /// 
    /// Edge cases handled:
    /// - New match detection (Goals reset to 0)
    /// - Stale memory pointers (sanity checks on read values)
    /// - Multiple GFxData instances with same name
    /// 
    /// References:
    /// - BakkesMod SDK: https://github.com/bakkesmodorg/BakkesModSDK
    /// - Rocket League class dumps: https://github.com/AJM55/RLObjectDumps
    /// - Unreal Engine GObjects/GNames: https://docs.unrealengine.com/udk/Three/UnrealScriptReference.html
    /// </summary>
    internal class RocketLeagueIntegration : Integration, IDisposable
    {
        private const string ProcessName = "RocketLeague";
        
        // Pattern scanning - these patterns find GObjects/GNames dynamically
        private static readonly byte[] GObjectsPattern = { 0x48, 0x8B, 0xC8, 0x48, 0x8B, 0x05, 0x00, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x0C, 0xC8 };
        private static readonly byte[] GNamesPattern1 = { 0x48, 0x8B, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x0C, 0xC1 };
        private static readonly byte[] GNamesPattern2 = { 0x49, 0x63, 0x06, 0x48, 0x8D, 0x55, 0xE8, 0x48, 0x8B, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x0C, 0xC1 };
        
        // Expected offset between GNames and GObjects (for validation/fallback)
        private const long ExpectedGNamesGObjectsOffset = 0x48;
        
        // GFxData_PRI_TA offsets
        private const int NameIndexOffset = 0x48;
        private const int ClassOffset = 0x50;
        private const int PlayerNameOffset = 0x98;
        private const int TeamOffset = 0xD0;
        
        // PRI_TA offsets (live stats)
        private const int PRI_ScoreOffset = 0x458;
        private const int PRI_GoalsOffset = 0x45C;
        private const int PRI_AssistsOffset = 0x464;

        // GFxData_PRI_TA to PRI_TA link offset
        private const int GfxDataToPriOffset = 0x138;
        private const int GfxDataToPriFallbackStart = 0x40;
        private const int GfxDataToPriFallbackEnd = 0x200;
        
        // GObjects/GNames structure offsets
        private const int GObjectsNumElementsOffset = 0x08;
        private const int FNameEntryNameOffset = 0x18;
        
        private IntPtr _processHandle = IntPtr.Zero;
        private IntPtr _baseAddress = IntPtr.Zero;
        private int _moduleSize = 0;
        private IntPtr _gObjectsAddress = IntPtr.Zero;
        private IntPtr _gNamesAddress = IntPtr.Zero;
        
        private CancellationTokenSource? _cts;
        private int _lastGoals = 0;
        private int _lastAssists = 0;
        private bool _initialStatsCaptured = false;
        private IntPtr _localPlayerPtr = IntPtr.Zero;  // GFxData_PRI_TA for name
        private IntPtr _localPriTaPtr = IntPtr.Zero;   // PRI_TA for live stats
        
        // P/Invoke
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
        
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        /// <summary>
        /// Starts the Rocket League integration by launching the monitoring loop.
        /// </summary>
        public override async Task Start()
        {
            _cts = new CancellationTokenSource();
            Log.Information("[RL] Starting Rocket League integration");
            
            await Task.Run(() => MonitorLoop(_cts.Token));
        }

        /// <summary>
        /// Shuts down the integration and releases process handles.
        /// </summary>
        public override Task Shutdown()
        {
            Log.Information("[RL] Shutting down Rocket League integration");
            _cts?.Cancel();
            
            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Main loop that attaches to the game process and monitors for goals.
        /// </summary>
        /// <param name="token">Cancellation token to stop the loop.</param>
        private void MonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!Attach())
                    {
                        Thread.Sleep(2000);
                        continue;
                    }
                    
                    MonitorGoals(token);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RL] Integration error: {ex.Message}\n{ex.StackTrace}");
                    Detach();
                    Thread.Sleep(2000);
                }
            }
        }

        /// <summary>
        /// Attaches to the Rocket League process and initializes memory reading.
        /// </summary>
        /// <returns>True if successfully attached, false otherwise.</returns>
        private bool Attach()
        {
            if (_processHandle != IntPtr.Zero)
            {
                try
                {
                    var proc = Process.GetProcessesByName(ProcessName).FirstOrDefault();
                    if (proc == null || proc.HasExited)
                    {
                        Detach();
                        return false;
                    }
                    return true;
                }
                catch
                {
                    Detach();
                    return false;
                }
            }
            
            var processes = Process.GetProcessesByName(ProcessName);
            var process = processes.FirstOrDefault();
            if (process == null)
            {
                return false;
            }
            
            try
            {
                _processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, process.Id);
                if (_processHandle == IntPtr.Zero)
                {
                    Log.Warning("[RL] Failed to open Rocket League process");
                    return false;
                }
                
                var mainModule = process.MainModule;
                if (mainModule == null)
                {
                    Log.Warning("[RL] Failed to get Rocket League main module");
                    Detach();
                    return false;
                }
                
                _baseAddress = mainModule.BaseAddress;
                _moduleSize = mainModule.ModuleMemorySize;
                
                Log.Information($"[RL] Attached to Rocket League PID={process.Id}");
                Log.Information($"[RL] Base: 0x{_baseAddress.ToInt64():X}, Size: 0x{_moduleSize:X}");
                
                // Find GObjects and GNames using pattern scanning
                if (!FindOffsetsWithPatternScan())
                {
                    Log.Error("[RL] Failed to find GObjects/GNames offsets via pattern scan");
                    Detach();
                    return false;
                }
                
                _lastGoals = 0;
                _initialStatsCaptured = false;
                _localPlayerPtr = IntPtr.Zero;
                _localPriTaPtr = IntPtr.Zero;
                
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"[RL] Failed to attach to Rocket League: {ex.Message}");
                Detach();
                return false;
            }
        }

        /// <summary>
        /// Finds GObjects and GNames addresses using pattern scanning.
        /// These are Unreal Engine structures needed to enumerate game objects.
        /// </summary>
        /// <returns>True if both addresses were found and validated.</returns>
        private bool FindOffsetsWithPatternScan()
        {
            Log.Information("[RL] Starting pattern scan for GObjects/GNames...");
            
            // Read module memory for pattern scanning
            byte[] moduleMemory = new byte[_moduleSize];
            if (!ReadProcessMemory(_processHandle, _baseAddress, moduleMemory, _moduleSize, out int bytesRead) || bytesRead != _moduleSize)
            {
                Log.Warning($"[RL] Failed to read module memory. Read {bytesRead}/{_moduleSize} bytes");
                return false;
            }
            
            // Find GObjects
            long gobjectsPatternAddr = ScanPattern(moduleMemory, GObjectsPattern, "GObjects");
            if (gobjectsPatternAddr >= 0)
            {
                _gObjectsAddress = ExtractGObjectsAddress(moduleMemory, gobjectsPatternAddr);
                Log.Information($"[RL] GObjects address: 0x{_gObjectsAddress.ToInt64():X}");
            }
            
            // Find GNames - try pattern 1 first
            long gnamesPatternAddr = ScanPattern(moduleMemory, GNamesPattern1, "GNames (Method 1)");
            if (gnamesPatternAddr >= 0)
            {
                _gNamesAddress = ExtractGNamesAddress1(moduleMemory, gnamesPatternAddr);
                Log.Information($"[RL] GNames address (method 1): 0x{_gNamesAddress.ToInt64():X}");
            }
            else
            {
                // Try pattern 2
                gnamesPatternAddr = ScanPattern(moduleMemory, GNamesPattern2, "GNames (Method 2)");
                if (gnamesPatternAddr >= 0)
                {
                    _gNamesAddress = ExtractGNamesAddress2(moduleMemory, gnamesPatternAddr);
                    Log.Information($"[RL] GNames address (method 2): 0x{_gNamesAddress.ToInt64():X}");
                }
            }
            
            // Validate and adjust if needed
            if (_gObjectsAddress != IntPtr.Zero && _gNamesAddress == IntPtr.Zero)
            {
                // Fallback: GNames is typically 0x48 bytes before GObjects
                _gNamesAddress = IntPtr.Subtract(_gObjectsAddress, (int)ExpectedGNamesGObjectsOffset);
                Log.Warning($"[RL] Using fallback GNames address: 0x{_gNamesAddress.ToInt64():X}");
            }
            
            bool success = _gObjectsAddress != IntPtr.Zero && _gNamesAddress != IntPtr.Zero;
            if (success)
            {
                // Validate GNames by reading index 0 which should be "None"
                string? testName = ReadGName(0);
                if (testName != "None")
                {
                    Log.Warning($"[RL] GNames validation failed - index 0 is '{testName}' (expected: 'None')");
                    return false;
                }
            }
            
            return success;
        }

        /// <summary>
        /// Scans memory for a byte pattern. Bytes with value 0x00 act as wildcards.
        /// </summary>
        /// <param name="memory">The memory buffer to search.</param>
        /// <param name="pattern">The byte pattern to find (0x00 = wildcard).</param>
        /// <param name="patternName">Name for logging purposes.</param>
        /// <returns>Offset where pattern was found, or -1 if not found.</returns>
        private long ScanPattern(byte[] memory, byte[] pattern, string patternName)
        {
            for (int i = 0; i < memory.Length - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    // 0x00 is wildcard
                    if (pattern[j] != 0x00 && memory[i + j] != pattern[j])
                    {
                        found = false;
                        break;
                    }
                }
                
                if (found)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Extracts the GObjects array address from a pattern match.
        /// </summary>
        /// <param name="memory">Module memory buffer.</param>
        /// <param name="patternOffset">Offset where the pattern was found.</param>
        /// <returns>The resolved GObjects address.</returns>
        private IntPtr ExtractGObjectsAddress(byte[] memory, long patternOffset)
        {
            // Pattern: 48 8B C8 48 8B 05 ?? ?? ?? ?? 48 8B 0C C8
            // Relative offset is at patternOffset + 6
            try
            {
                long offsetAddr = patternOffset + 6;
                int relativeOffset = BitConverter.ToInt32(memory, (int)offsetAddr);
                long finalAddr = _baseAddress.ToInt64() + offsetAddr + relativeOffset + 4;
                return new IntPtr(finalAddr);
            }
            catch (Exception ex)
            {
                Log.Warning($"[RL] Failed to extract GObjects address: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Extracts the GNames array address using pattern method 1.
        /// </summary>
        /// <param name="memory">Module memory buffer.</param>
        /// <param name="patternOffset">Offset where the pattern was found.</param>
        /// <returns>The resolved GNames address.</returns>
        private IntPtr ExtractGNamesAddress1(byte[] memory, long patternOffset)
        {
            // Pattern: 48 8B 0D ?? ?? ?? ?? 48 8B 0C C1
            // Relative offset is at patternOffset + 3
            try
            {
                long offsetAddr = patternOffset + 3;
                int relativeOffset = BitConverter.ToInt32(memory, (int)offsetAddr);
                long finalAddr = _baseAddress.ToInt64() + offsetAddr + relativeOffset + 4;
                return new IntPtr(finalAddr);
            }
            catch (Exception ex)
            {
                Log.Warning($"[RL] Failed to extract GNames address (method 1): {ex.Message}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Extracts the GNames array address using pattern method 2 (fallback).
        /// </summary>
        /// <param name="memory">Module memory buffer.</param>
        /// <param name="patternOffset">Offset where the pattern was found.</param>
        /// <returns>The resolved GNames address.</returns>
        private IntPtr ExtractGNamesAddress2(byte[] memory, long patternOffset)
        {
            // Pattern: 49 63 06 48 8D 55 E8 48 8B 0D ?? ?? ?? ?? 48 8B 0C C1
            // Relative offset is at patternOffset + 10
            try
            {
                long offsetAddr = patternOffset + 10;
                int relativeOffset = BitConverter.ToInt32(memory, (int)offsetAddr);
                long finalAddr = _baseAddress.ToInt64() + offsetAddr + relativeOffset + 4;
                return new IntPtr(finalAddr);
            }
            catch (Exception ex)
            {
                Log.Warning($"[RL] Failed to extract GNames address (method 2): {ex.Message}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Detaches from the game process and resets all state.
        /// </summary>
        private void Detach()
        {
            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
            _baseAddress = IntPtr.Zero;
            _gObjectsAddress = IntPtr.Zero;
            _gNamesAddress = IntPtr.Zero;
            _lastGoals = 0;
            _lastAssists = 0;
            _initialStatsCaptured = false;
            _localPlayerPtr = IntPtr.Zero;
        }

        /// <summary>
        /// Monitors the local player's goal count and adds bookmarks when goals are scored.
        /// Polls every 50ms for instant detection, refreshes player every ~2 seconds.
        /// </summary>
        /// <param name="token">Cancellation token to stop monitoring.</param>
        private void MonitorGoals(CancellationToken token)
        {
            int refreshCounter = 0;
            
            while (!token.IsCancellationRequested && _processHandle != IntPtr.Zero)
            {
                try
                {
                    // Search for player every ~2 seconds to catch new matches/instances
                    if (_localPriTaPtr == IntPtr.Zero || refreshCounter % 40 == 0)
                    {
                        FindLocalPlayer();
                        if (_localPriTaPtr == IntPtr.Zero)
                        {
                            refreshCounter++;
                            Thread.Sleep(500); // Wait longer when no player found
                            continue;
                        }
                    }
                    refreshCounter++;
                    
                    // Read current stats from PRI_TA (live stats)
                    int currentGoals = Read<int>(_localPriTaPtr + PRI_GoalsOffset);
                    int currentAssists = Read<int>(_localPriTaPtr + PRI_AssistsOffset);
                    int score = Read<int>(_localPriTaPtr + PRI_ScoreOffset);
                    
                    // Sanity check - detect garbage data (e.g., when game loses focus)
                    if (currentGoals < 0 || currentGoals > 50 || currentAssists < 0 || currentAssists > 50 || score < 0 || score > 10000)
                    {
                        // Invalid data - pointer likely stale, force rescan
                        _localPlayerPtr = IntPtr.Zero;
                        _localPriTaPtr = IntPtr.Zero;
                        _initialStatsCaptured = false;
                        Thread.Sleep(500);
                        continue;
                    }
                    
                    if (!_initialStatsCaptured)
                    {
                        _lastGoals = currentGoals;
                        _lastAssists = currentAssists;
                        _initialStatsCaptured = true;
                        string playerName = _localPlayerPtr != IntPtr.Zero ? ReadPlayerName(_localPlayerPtr) : "Unknown";
                        Log.Information($"[RL] Initial stats for local player '{playerName}': Goals={currentGoals}, Assists={currentAssists}, Score={score}");
                    }
                    else if (currentGoals == _lastGoals + 1)
                    {
                        // Single goal scored (normal case at 50ms polling)
                        Log.Information($"[RL] YOU SCORED! Goals: {_lastGoals} -> {currentGoals}");
                        AddBookmark(BookmarkType.Goal);
                        _lastGoals = currentGoals;
                    }
                    else if (currentGoals > _lastGoals + 1)
                    {
                        // Multiple goals at once = likely stale data, just update without bookmark
                        _lastGoals = currentGoals;
                    }
                    else if (currentGoals < _lastGoals)
                    {
                        // New match started, reset
                        Log.Information($"[RL] New match detected, resetting stats");
                        _lastGoals = currentGoals;
                    }
                    
                    // Check for assists
                    if (currentAssists == _lastAssists + 1)
                    {
                        Log.Information($"[RL] YOU ASSISTED! Assists: {_lastAssists} -> {currentAssists}");
                        AddBookmark(BookmarkType.Assist);
                        _lastAssists = currentAssists;
                    }
                    else if (currentAssists > _lastAssists + 1)
                    {
                        // Multiple assists at once = likely stale data, just update without bookmark
                        _lastAssists = currentAssists;
                    }
                    else if (currentAssists < _lastAssists)
                    {
                        // New match started, reset assists
                        _lastAssists = currentAssists;
                    }
                    
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RL] Error monitoring goals: {ex.Message}");
                    _localPlayerPtr = IntPtr.Zero; // Force refresh
                    _localPriTaPtr = IntPtr.Zero;
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>
        /// Identifies the local player by finding their GFxData_PRI_TA (for name) and PRI_TA (for live stats).
        /// Uses name frequency analysis since the local player's name appears most often in UI data.
        /// </summary>
        private void FindLocalPlayer()
        {
            var players = FindPlayerInstances();
            
            if (players.Count == 0)
            {
                _localPlayerPtr = IntPtr.Zero;
                _localPriTaPtr = IntPtr.Zero;
                return;
            }
            
            // Collect all instances by type
            var priTaInstances = new List<IntPtr>();
            var gfxDataInstances = new List<(IntPtr ptr, string name, int team)>();
            var nameCounts = new Dictionary<string, int>();
            
            foreach (var playerPtr in players)
            {
                IntPtr classPtr = Read<IntPtr>(playerPtr + ClassOffset);
                int classNameIndex = classPtr != IntPtr.Zero ? Read<int>(classPtr + NameIndexOffset) : 0;
                string? className = ReadGName(classNameIndex);
                
                if (className == "PRI_TA")
                {
                    priTaInstances.Add(playerPtr);
                }
                else if (className == "GFxData_PRI_TA")
                {
                    string name = ReadPlayerName(playerPtr);
                    int team = Read<int>(playerPtr + TeamOffset);
                    
                    if (!string.IsNullOrEmpty(name))
                    {
                        gfxDataInstances.Add((playerPtr, name, team));
                        
                        // Count name occurrences - local player's name appears most often (cached in UI)
                        if (!nameCounts.ContainsKey(name))
                            nameCounts[name] = 0;
                        nameCounts[name]++;
                    }
                }
            }
            
            // Find the local player's name (most frequent in GFxData_PRI_TA)
            string? localPlayerName = null;
            int maxCount = 0;
            foreach (var kvp in nameCounts)
            {
                if (kvp.Value > maxCount)
                {
                    maxCount = kvp.Value;
                    localPlayerName = kvp.Key;
                }
            }
            
            if (localPlayerName != null)
            {
                // Collect all candidates with their stats
                var candidates = new List<(IntPtr gfxPtr, IntPtr priPtr, int goals, int score)>();
                
                foreach (var (ptr, name, team) in gfxDataInstances)
                {
                    if (name == localPlayerName && (team == 0 || team == 1))
                    {
                        IntPtr linkedPri = Read<IntPtr>(ptr + GfxDataToPriOffset);
                        
                        if (priTaInstances.Contains(linkedPri))
                        {
                            int priGoals = Read<int>(linkedPri + PRI_GoalsOffset);
                            int priScore = Read<int>(linkedPri + PRI_ScoreOffset);
                            candidates.Add((ptr, linkedPri, priGoals, priScore));
                        }
                    }
                }
                
                // If we have a valid PRI, check if a NEW match started (another PRI has Goals=0)
                if (_localPriTaPtr != IntPtr.Zero && priTaInstances.Contains(_localPriTaPtr))
                {
                    // Check if any other candidate has Goals=0 (fresh match)
                    var freshMatch = candidates.FirstOrDefault(c => c.priPtr != _localPriTaPtr && c.goals == 0);
                    if (freshMatch.priPtr != IntPtr.Zero)
                    {
                        // New match detected - switch to fresh PRI
                        _localPlayerPtr = freshMatch.gfxPtr;
                        _localPriTaPtr = freshMatch.priPtr;
                        return;
                    }
                    // No fresh match, keep current PRI
                    return;
                }
                
                // No valid PRI yet - select candidate with lowest goals (most likely current match)
                if (candidates.Count > 0)
                {
                    var best = candidates.OrderBy(c => c.goals).ThenBy(c => c.score).First();
                    _localPlayerPtr = best.gfxPtr;
                    _localPriTaPtr = best.priPtr;
                }
            }
            
            // Fallback: scan for PRI link if candidate selection didn't find one
            if (_localPriTaPtr == IntPtr.Zero && _localPlayerPtr != IntPtr.Zero && priTaInstances.Count > 0)
            {
                for (int offset = GfxDataToPriFallbackStart; offset <= GfxDataToPriFallbackEnd; offset += 8)
                {
                    IntPtr potentialPri = Read<IntPtr>(_localPlayerPtr + offset);
                    if (priTaInstances.Contains(potentialPri))
                    {
                        _localPriTaPtr = potentialPri;
                        break;
                    }
                }
            }
            
        }

        /// <summary>
        /// Iterates through GObjects to find all player-related instances (PRI_TA, GFxData_PRI_TA, etc.).
        /// </summary>
        /// <returns>List of pointers to player-related game objects.</returns>
        private List<IntPtr> FindPlayerInstances()
        {
            var players = new List<IntPtr>();
            
            try
            {
                IntPtr objectsArrayPtr = Read<IntPtr>(_gObjectsAddress);
                int objectCount = Read<int>(_gObjectsAddress + GObjectsNumElementsOffset);
                
                if (objectsArrayPtr == IntPtr.Zero)
                {
                    Log.Warning("[RL] GObjects.Objects pointer is null");
                    return players;
                }
                
                if (objectCount <= 0 || objectCount > 500000)
                {
                    Log.Warning($"[RL] Invalid object count: {objectCount}");
                    return players;
                }
                
                for (int i = 0; i < objectCount; i++)
                {
                    IntPtr objectPtr = Read<IntPtr>(objectsArrayPtr + (i * 8));
                    if (objectPtr == IntPtr.Zero)
                        continue;
                    
                    IntPtr classPtr = Read<IntPtr>(objectPtr + ClassOffset);
                    if (classPtr == IntPtr.Zero)
                        continue;
                    
                    int classNameIndex = Read<int>(classPtr + NameIndexOffset);
                    string? className = ReadGName(classNameIndex);
                    
                    if (className == "PRI_TA" || className == "GFxData_PRI_TA" || 
                        className == "PlayerController_TA" || className == "PlayerController")
                    {
                        players.Add(objectPtr);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RL] Error finding player instances: {ex.Message}\n{ex.StackTrace}");
            }
            
            return players;
        }

        /// <summary>
        /// Reads a value of type T from the game's process memory.
        /// </summary>
        /// <typeparam name="T">The struct type to read.</typeparam>
        /// <param name="address">The memory address to read from.</param>
        /// <returns>The value read, or default(T) on failure.</returns>
        private T Read<T>(IntPtr address) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] buffer = new byte[size];
            
            if (!ReadProcessMemory(_processHandle, address, buffer, size, out _))
            {
                return default;
            }
            
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// Reads a name string from the GNames array by index.
        /// </summary>
        /// <param name="nameIndex">The FName index to look up.</param>
        /// <returns>The name string, or null if not found.</returns>
        private string? ReadGName(int nameIndex)
        {
            try
            {
                if (nameIndex < 0 || nameIndex > 500000)
                    return null;
                
                IntPtr gNamesArrayPtr = Read<IntPtr>(_gNamesAddress);
                if (gNamesArrayPtr == IntPtr.Zero)
                    gNamesArrayPtr = _gNamesAddress;
                
                IntPtr nameEntryPtr = Read<IntPtr>(gNamesArrayPtr + (nameIndex * 8));
                if (nameEntryPtr == IntPtr.Zero)
                    return null;
                
                byte[] buffer = new byte[256];
                if (!ReadProcessMemory(_processHandle, nameEntryPtr + FNameEntryNameOffset, buffer, buffer.Length, out _))
                    return null;
                
                // Try Unicode first (wchar_t)
                string unicodeResult = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Split('\0')[0];
                
                // If Unicode gives garbage (non-printable chars), try ASCII
                if (string.IsNullOrEmpty(unicodeResult) || !IsPrintableString(unicodeResult))
                {
                    string asciiResult = Encoding.ASCII.GetString(buffer).TrimEnd('\0').Split('\0')[0];
                    if (!string.IsNullOrEmpty(asciiResult) && IsPrintableString(asciiResult))
                        return asciiResult;
                }
                
                return unicodeResult;
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// Checks if a string contains only printable ASCII characters (32-126).
        /// </summary>
        /// <param name="s">The string to check.</param>
        /// <returns>True if all characters are printable ASCII.</returns>
        private static bool IsPrintableString(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (c < 32 || c > 126) // Basic printable ASCII range
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Reads the player name from a GFxData_PRI_TA instance.
        /// </summary>
        /// <param name="playerPtr">Pointer to the GFxData_PRI_TA object.</param>
        /// <returns>The player name, or empty string on failure.</returns>
        private string ReadPlayerName(IntPtr playerPtr)
        {
            try
            {
                // PlayerName at 0x98 is a pointer to FString
                IntPtr namePtr = Read<IntPtr>(playerPtr + PlayerNameOffset);
                if (namePtr == IntPtr.Zero)
                    return string.Empty;
                
                byte[] buffer = new byte[64];
                if (!ReadProcessMemory(_processHandle, namePtr, buffer, buffer.Length, out _))
                    return string.Empty;
                
                return Encoding.Unicode.GetString(buffer).TrimEnd('\0').Split('\0')[0];
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Adds a bookmark to the current recording.
        /// </summary>
        /// <param name="type">The type of bookmark (e.g., Kill for goals).</param>
        private static void AddBookmark(BookmarkType type)
        {
            if (Settings.Instance.State.Recording == null)
            {
                Log.Warning($"[RL] No recording active, skipping {type} bookmark");
                return;
            }

            var bookmark = new Bookmark
            {
                Type = type,
                Time = DateTime.Now - Settings.Instance.State.Recording.StartTime
            };
            Settings.Instance.State.Recording.Bookmarks.Add(bookmark);
            Log.Information($"[RL] BOOKMARK ADDED: {type} at {bookmark.Time}");
        }

        /// <summary>
        /// Disposes resources by shutting down the integration.
        /// </summary>
        public void Dispose()
        {
            Shutdown().Wait();
        }
    }
}
