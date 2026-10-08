using System.Diagnostics;
using System.Globalization;
using Serilog;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.Rust
{
    internal class RustIntegration : LogTailIntegration
    {
        protected override string LogPrefix => "Rust";

        // Death lines come from the server's echo, combatlog rows can also contain "you died first"
        private const string DeathMarker = "|You died:";

        // Rows can show up in several dumps, so the same target within this window is one kill
        private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(1.5);

        // Binds that also dump the combat log, keys players press after most fights
        private static readonly string[] TriggerCommands = ["+jump", "+reload", "inventory.toggle"];
        private const string CombatLogCommand = "combatlog";

        private static int _exitWatcherRunning;

        private DateTime? _combatLogTime;
        private readonly List<(string Target, DateTime Time)> _kills = [];

        public override Task Start()
        {
            string? keysPath = ResolveGamePath(Path.Combine("cfg", "keys.cfg"));
            if (keysPath != null)
            {
                // Rust loads keys.cfg a few seconds after launch and rewrites it on quit, so try now and again after exit
                AddCombatLogBinds(keysPath);
                WatchForGameExit(keysPath);
            }
            return base.Start();
        }

        protected override string? ResolveLogPath() => ResolveGamePath("output_log.txt");

        private string? ResolveGamePath(string relativePath)
        {
            if (string.IsNullOrEmpty(ExePath)) return null;
            string? dir = Path.GetDirectoryName(ExePath);
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, relativePath);
        }

        private static void WatchForGameExit(string keysPath)
        {
            if (Interlocked.Exchange(ref _exitWatcherRunning, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (Process.GetProcessesByName("RustClient").Length > 0)
                        await Task.Delay(5000);
                    AddCombatLogBinds(keysPath);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[Rust] Failed to update keys.cfg after exit: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _exitWatcherRunning, 0);
                }
            });
        }

        private static void AddCombatLogBinds(string keysPath)
        {
            try
            {
                if (!File.Exists(keysPath))
                {
                    Log.Warning($"[Rust] keys.cfg not found at {keysPath}");
                    return;
                }
                string text = File.ReadAllText(keysPath);
                string newline = text.Contains("\r\n") ? "\r\n" : "\n";
                string[] lines = text.Split(newline);
                var changedKeys = new List<string>();
                var boundKeys = new List<string>();

                for (int i = 0; i < lines.Length; i++)
                {
                    // Rust writes binds as: bind <key> <cmd>;<cmd>
                    string[] parts = lines[i].Split(' ', 3);
                    if (parts.Length < 3 || parts[0] != "bind") continue;
                    string commands = parts[2];
                    if (commands.Contains('"') || commands.StartsWith('~')) continue;

                    string[] list = commands.Split(';');
                    if (!list.Any(TriggerCommands.Contains)) continue;
                    if (list.Contains(CombatLogCommand))
                    {
                        boundKeys.Add(parts[1]);
                        continue;
                    }

                    lines[i] += ";" + CombatLogCommand;
                    changedKeys.Add(parts[1]);
                }

                if (changedKeys.Count == 0)
                {
                    if (boundKeys.Count > 0)
                        Log.Information($"[Rust] keys.cfg already has {CombatLogCommand} on: {string.Join(", ", boundKeys)}");
                    else
                        Log.Warning("[Rust] No jump, reload or inventory bind found in keys.cfg, kills will not be detected");
                    return;
                }

                string backupPath = keysPath + ".segra-backup";
                if (!File.Exists(backupPath))
                    File.Copy(keysPath, backupPath);

                string tempPath = keysPath + ".segra-tmp";
                File.WriteAllText(tempPath, string.Join(newline, lines));
                File.Move(tempPath, keysPath, overwrite: true);
                Log.Information($"[Rust] Added {CombatLogCommand} to binds: {string.Join(", ", changedKeys)}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[Rust] Failed to update keys.cfg: {ex.Message}");
            }
        }

        protected override void OnLogOpened(string path)
        {
            _combatLogTime = null;
            _kills.Clear();
        }

        protected override void ProcessLine(string line)
        {
            DateTime? lineTime = ParseLineTime(line);
            if (lineTime != null)
            {
                string message = line[(line.IndexOf('|', line.IndexOf('|') + 1) + 1)..];
                _combatLogTime = message.StartsWith("time ") && message.Contains(" attacker ") ? lineTime : null;

                if (line.Contains(DeathMarker, StringComparison.Ordinal))
                    AddBookmarkAt(BookmarkType.Death, lineTime.Value, message.Trim());
                return;
            }

            if (_combatLogTime != null)
                ProcessCombatLogRow(line, _combatLogTime.Value);
        }

        // Columns: time attacker [id] target [id] weapon ammo area distance old_hp new_hp info hits integrity travel mismatch desync
        private void ProcessCombatLogRow(string line, DateTime dumpTime)
        {
            string[] cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 14 || cols[1] != "you" || !cols[0].EndsWith('s')) return;
            // The server formats the age with its own culture, so it can use a comma
            if (!float.TryParse(cols[0][..^1].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float ago)) return;

            // Players show as player_<id>, other prefabs like player_corpse must not count
            string target = cols[2].StartsWith("player_") ? cols[2] : cols[3] == "player" ? "player_" + cols[4] : "";
            if (target.Length <= 7 || !target[7..].All(char.IsDigit)) return;
            if (!cols[3..^5].Any(c => c == "killed" || c == "(killed)")) return;

            DateTime killTime = dumpTime.AddSeconds(-ago);
            if (_kills.Any(k => k.Target == target && (k.Time - killTime).Duration() < DuplicateWindow)) return;
            _kills.Add((target, killTime));

            AddBookmarkAt(BookmarkType.Kill, killTime, $"on {target}, {ago:F2}s before combatlog dump");
        }

        private void AddBookmarkAt(BookmarkType type, DateTime time, string detail)
        {
            var recording = AppState.Instance.Recording;
            if (recording == null) return;
            TimeSpan bookmarkTime = time - recording.StartTime;
            if (bookmarkTime < TimeSpan.Zero) return;

            recording.AddBookmark(new Bookmark { Type = type, Time = bookmarkTime });
            Log.Information($"[{LogPrefix}] BOOKMARK ADDED: {type} at {bookmarkTime} ({detail})");
        }

        // Lines Rust logs start with "2026-05-18T21:29:29.994Z|0xad1c|"
        private static DateTime? ParseLineTime(string line)
        {
            int bar = line.IndexOf('|');
            if (bar < 20 || bar > 30 || line.IndexOf('|', bar + 1) < 0) return null;
            if (!DateTime.TryParse(line.AsSpan(0, bar), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime utc)) return null;
            return utc.ToLocalTime();
        }
    }
}
