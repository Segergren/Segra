using Serilog;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.RainbowSixSiege
{
    internal class RainbowSixSiegeIntegration : Integration
    {
        private const int PrepPhaseSeconds = 45;
        private const int ActionPhaseSeconds = 180;
        // The defuse clock already reads 0:44 at the moment of the plant
        private const int DefuserSeconds = 44;

        private readonly System.Timers.Timer checkTimer = new(2500);
        private readonly HashSet<string> processedFiles = [];
        private string? replayFolder;
        private DateTime startedAt;

        public RainbowSixSiegeIntegration()
        {
            checkTimer.Elapsed += (_, _) => TimerTick();
        }

        public override Task Start()
        {
            var exeFolder = Path.GetDirectoryName(ExePath);
            var gameFolder = exeFolder == null ? null : FindGameFolder(exeFolder);
            if (gameFolder == null)
            {
                Log.Warning($"Rainbow Six Siege install folder not found for {ExePath}");
                return Task.CompletedTask;
            }

            replayFolder = Path.Combine(gameFolder, "MatchReplay");
            startedAt = DateTime.Now;

            Log.Information($"Initializing Rainbow Six Siege replay integration. Watching {replayFolder}");
            checkTimer.Start();
            return Task.CompletedTask;
        }

        private static string? FindGameFolder(string exeFolder)
        {
            foreach (var folder in CandidateGameFolders(exeFolder))
            {
                if (Directory.Exists(Path.Combine(folder, "MatchReplay")) || File.Exists(Path.Combine(folder, "RainbowSix_BE.exe")))
                    return folder;
            }

            return null;
        }

        private static IEnumerable<string> CandidateGameFolders(string exeFolder)
        {
            yield return exeFolder;

            if (!OperatingSystem.IsWindows())
                yield break;

            foreach (var hive in new[] { @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", @"SOFTWARE\Ubisoft\Launcher\Installs" })
            {
                using var installs = Registry.LocalMachine.OpenSubKey(hive);
                foreach (var id in installs?.GetSubKeyNames() ?? [])
                {
                    using var install = installs!.OpenSubKey(id);
                    if (install?.GetValue("InstallDir") is string dir && dir.Contains("Rainbow Six Siege", StringComparison.OrdinalIgnoreCase))
                        yield return dir;
                }
            }

            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var libraries = Path.Combine(steam?.GetValue("SteamPath") as string ?? "", "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraries))
                yield break;

            foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"([^\"]+)\""))
            {
                var common = Path.Combine(match.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common");
                if (!Directory.Exists(common))
                    continue;

                foreach (var dir in Directory.GetDirectories(common, "Tom Clancy's Rainbow Six Siege*"))
                    yield return dir;
            }
        }

        public override Task Shutdown()
        {
            Log.Information("Stopping Rainbow Six Siege replay integration.");
            checkTimer.Stop();
            return Task.CompletedTask;
        }

        private void TimerTick()
        {
            lock (processedFiles)
            {
                try
                {
                    if (replayFolder == null || !Directory.Exists(replayFolder))
                        return;

                    foreach (var file in Directory.EnumerateFiles(replayFolder, "*.rec", SearchOption.AllDirectories))
                    {
                        if (processedFiles.Contains(file))
                            continue;

                        var info = new FileInfo(file);
                        if (info.CreationTime < startedAt)
                        {
                            processedFiles.Add(file);
                            continue;
                        }

                        if (DateTime.Now - info.LastWriteTime < TimeSpan.FromSeconds(3))
                            continue;

                        processedFiles.Add(file);
                        ProcessRound(file);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Rainbow Six Siege integration encountered an error: {ex.Message}");
                }
            }
        }

        private void ProcessRound(string file)
        {
            Log.Information($"New Rainbow Six Siege replay: {file}");
            ReplayRound round;
            try
            {
                round = ReplayReader.Read(file);
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to parse Rainbow Six Siege replay {file}: {ex.Message}");
                return;
            }

            // The header roster uses the kill feed spelling; the in-stream profile match can carry a different display name
            var me = round.Players.FirstOrDefault(p => p.Id != 0 && p.Id == round.RecordingPlayerId)?.Username
                ?? round.Players.FirstOrDefault(p => !string.IsNullOrEmpty(p.ProfileId) && p.ProfileId == round.RecordingProfileId)?.Username;
            if (me == null)
            {
                Log.Warning($"Rainbow Six Siege round {round.RoundNumber + 1}: recording player not found in roster, skipping");
                return;
            }

            var recording = AppState.Instance.Recording;
            if (recording == null)
                return;

            var actionStart = round.Timestamp.AddSeconds(PrepPhaseSeconds);
            DateTime? planted = null;
            var added = 0;

            foreach (var update in round.Events)
            {
                var time = planted?.AddSeconds(DefuserSeconds - update.TimeInSeconds)
                    ?? actionStart.AddSeconds(ActionPhaseSeconds - update.TimeInSeconds);

                switch (update.Type)
                {
                    case ReplayEventType.DefuserPlantComplete:
                        planted = time;
                        break;
                    case ReplayEventType.Kill when update.Username == me && update.Target != me:
                        added += AddBookmark(recording, BookmarkType.Kill, update.Headshot ? BookmarkSubtype.Headshot : null, time);
                        break;
                    case ReplayEventType.Kill when update.Target == me:
                    case ReplayEventType.Death when update.Username == me:
                        added += AddBookmark(recording, BookmarkType.Death, null, time);
                        break;
                }
            }

            Log.Information($"Rainbow Six Siege round {round.RoundNumber + 1}: added {added} bookmarks");
        }

        private static int AddBookmark(Recording recording, BookmarkType type, BookmarkSubtype? subtype, DateTime time)
        {
            var offset = time - recording.StartTime;
            if (offset < TimeSpan.Zero)
                return 0;

            recording.AddBookmark(new Bookmark
            {
                Type = type,
                Subtype = subtype,
                Time = offset
            });
            return 1;
        }
    }
}
