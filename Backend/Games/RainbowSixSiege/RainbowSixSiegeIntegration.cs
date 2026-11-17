using Serilog;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using Segra.Backend.Core.Models;
using Segra.Backend.Media;

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
        private readonly MatchTracker matches = new();
        // Each match has its own replay folder
        private string? matchFolder;
        private int matchRound = -1;
        private int matchKills;
        private int matchDeaths;

        // r6-dissect's worldid table, plus reworked maps identified from recordings
        private static readonly Dictionary<ulong, string> MapNames = new()
        {
            [837214085] = "Clubhouse",
            [1378191338] = "Kafe Dostoyevsky",
            [1460220617] = "Kanal",
            [1767965020] = "Yacht",
            [2609218856] = "Presidential Plane",
            [2609221242] = "Consulate",
            [2697268122] = "Bartlett University",
            [42090092951] = "Coastline",
            [53627213396] = "Tower",
            [88107330328] = "Villa",
            [126196841359] = "Fortress",
            [127951053400] = "Hereford Base",
            [199824623654] = "Theme Park",
            [231702797556] = "Oregon",
            [237873412352] = "House",
            [259816839773] = "Chalet",
            [276279025182] = "Skyscraper",
            [305979357167] = "Border",
            [329867321446] = "Favela",
            [355496559878] = "Bank",
            [362605108559] = "Outback",
            [365284490964] = "Emerald Plains",
            [270063334510] = "Stadium Bravo",
            [378595635123] = "Nighthaven Labs",
            [379218689149] = "Consulate",
            [388073319671] = "Lair",
            [405306299908] = "Stadium",
            [413779563590] = "Bank",
            [407987100456] = "Border",
            [407558616688] = "Chalet",
            [407193663917] = "Clubhouse",
            [413845419788] = "Kafe Dostoyevsky",
            [417890697769] = "Lair",
            [418119057546] = "Nighthaven Labs",
            [418126004176] = "Consulate",
            [434715462383] = "Oregon",
            [441825219764] = "Outback",
            [398899676157] = "Fortress",
            [409325881472] = "Villa",
            [419965653950] = "Calypso Casino",
            [423767322185] = "Skyscraper",
            [430788891316] = "Theme Park",
            [436375283234] = "Coastline",
            [439976373310] = "Presidential Plane",
            [441408792952] = "Kanal",
            [441842102525] = "Emerald Plains",
            [446546313105] = "House",
            [447442858240] = "Stadium Bravo",
            [447445273533] = "Stadium Alpha",
            [453996545976] = "Favela",
            [454245490351] = "Tower",
            [454336658645] = "Yacht",
        };

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
            // The last round's replay lands about 10 s after the match, often just as the game is closed
            TimerTick(final: true);
            return Task.CompletedTask;
        }

        private void TimerTick(bool final = false)
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

                        if (!final && DateTime.Now - info.LastWriteTime < TimeSpan.FromSeconds(3))
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
            var kills = 0;
            var deaths = 0;

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
                        kills++;
                        added += AddBookmark(recording, BookmarkType.Kill, update.Headshot ? BookmarkSubtype.Headshot : null, time);
                        break;
                    case ReplayEventType.Kill when update.Target == me:
                    case ReplayEventType.Death when update.Username == me:
                        deaths++;
                        added += AddBookmark(recording, BookmarkType.Death, null, time);
                        break;
                }
            }

            Log.Information($"Rainbow Six Siege round {round.RoundNumber + 1}: added {added} bookmarks");
            TrackMatch(file, round, me, kills, deaths);
        }

        private void TrackMatch(string file, ReplayRound round, string me, int kills, int deaths)
        {
            string? folder = Path.GetDirectoryName(file);
            if (folder != matchFolder)
            {
                matches.Close();
                matchFolder = folder;
                matchRound = -1;
                matchKills = 0;
                matchDeaths = 0;
                if (!MapNames.ContainsKey(round.WorldId))
                    Log.Information($"Rainbow Six Siege map {round.WorldId} has no name yet");
                // The header time is when the round's prep phase started
                matches.Start(m =>
                {
                    m.Map = MapNames.GetValueOrDefault(round.WorldId);
                    m.Mode = FormatMatchType(round.MatchType);
                }, DateTime.Now - round.Timestamp);
            }

            matchKills += kills;
            matchDeaths += deaths;
            // Rounds are read in order, but a late file must not take the score back
            if (round.RoundNumber < matchRound)
                return;
            matchRound = round.RoundNumber;

            int? team = round.Players.FirstOrDefault(p => p.Username == me)?.Team;
            int? score = team is 0 or 1 ? round.TeamScores[team.Value] : null;
            int? opponentScore = team is 0 or 1 ? round.TeamScores[1 - team.Value] : null;
            void Apply(GameMatch m)
            {
                m.Score = score;
                m.OpponentScore = opponentScore;
                m.Kills = matchKills;
                m.Deaths = matchDeaths;
            }

            if (score != null && opponentScore != null && IsMatchOver(round))
            {
                bool wasRunning = matches.IsRunning;
                matches.End(m =>
                {
                    Apply(m);
                    m.Result = score > opponentScore ? MatchResult.Win : MatchResult.Loss;
                });
                if (wasRunning)
                    MatchSplitService.SplitAfterMatch();
            }
            else
            {
                matches.Update(Apply);
            }
        }

        // First to half the rounds plus one; at a tie on half each, overtime adds half the overtime rounds
        private static bool IsMatchOver(ReplayRound round)
        {
            int half = round.RoundsPerMatch / 2;
            if (half <= 0)
                return false;

            int a = round.TeamScores[0];
            int b = round.TeamScores[1];
            int target = a >= half && b >= half ? half + 1 + round.RoundsPerMatchOvertime / 2 : half + 1;
            return Math.Max(a, b) >= target;
        }

        private static string? FormatMatchType(int matchType) => matchType switch
        {
            1 => "Quick Match",
            2 => "Ranked",
            3 or 4 => "Custom",
            8 => "Standard",
            9 => "Unranked",
            _ => null,
        };

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
