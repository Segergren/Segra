using Segra.Backend.Core.Models;
using Serilog;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Segra.Backend.Games.Pubg
{
    internal partial class PubgIntegration : Integration, IDisposable
    {
        private readonly System.Timers.Timer checkTimer;
        private readonly string pubgReplayFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"TslGame\Saved\Demos");

        private HashSet<string> previousReplayDirs;

        public class PubgMatchInfo
        {
            public long Timestamp { get; set; }
            public string? RecordUserNickName { get; set; }
        }

        public class PubgEventDetails
        {
            [JsonPropertyName("time1")]
            public int Time { get; set; }
            [JsonPropertyName("data")]
            public string? Data { get; set; }
        }

        public PubgIntegration()
        {
            checkTimer = new System.Timers.Timer
            {
                Interval = 2500
            };
            checkTimer.Elapsed += (sender, args) => TimerTick();
            previousReplayDirs = [];
        }

        public override Task Start()
        {
            if (!Directory.Exists(pubgReplayFolder))
                Directory.CreateDirectory(pubgReplayFolder);

            previousReplayDirs = Directory.GetDirectories(pubgReplayFolder).ToHashSet();
            Log.Information("Initializing PUBG data integration.");

            checkTimer.Start();
            return Task.CompletedTask;
        }

        public override Task Shutdown()
        {
            Log.Information("Stopping PUBG data integration.");
            checkTimer.Stop();
            return Task.CompletedTask;
        }

        private void TimerTick()
        {
            try
            {
                var currentDirs = Directory.GetDirectories(pubgReplayFolder).ToHashSet();
                var newDirs = currentDirs.Except(previousReplayDirs).ToList();
                previousReplayDirs = currentDirs;

                if (newDirs.Count == 0) return;

                foreach (var directory in newDirs)
                {
                    Log.Information($"New PUBG replay: {directory}");
                    var processedVictims = new HashSet<string>();
                    Thread.Sleep(500);

                    var infoPath = Path.Combine(directory, "PUBG.replayinfo");
                    var matchJson = ReadJson(infoPath);
                    var matchInfo = JsonSerializer.Deserialize<PubgMatchInfo>(matchJson);

                    if (matchInfo is null)
                    {
                        Log.Warning("Failed to parse match info from {InfoPath}", infoPath);
                        continue;
                    }

                    ProcessDownedPlayers(directory, matchInfo, processedVictims);
                    ProcessKills(directory, matchInfo, processedVictims);
                    ProcessPlayerDeath(directory, matchInfo);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"PUBG integration encountered an error: {ex.Message}");
            }
        }

        private void ProcessDownedPlayers(string folder, PubgMatchInfo matchInfo, HashSet<string> trackedVictims)
        {
            var downFiles = Directory.GetFiles(Path.Combine(folder, "events"), "groggy*");
            foreach (var filePath in downFiles)
            {
                var eventJson = ReadJson(filePath);
                var details = JsonSerializer.Deserialize<PubgEventDetails>(eventJson);

                if (details is null || details.Data is null)
                {
                    Log.Warning("Failed to parse event details from {FilePath}", filePath);
                    continue;
                }

                var rawData = DecodeBase64(details.Data);
                var dataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(rawData);
                var dataList = dataDict?.Values?.ToList();

                var instigator = dataList?[1]?.ToString();
                var victim = dataList?[3]?.ToString();

                if (instigator != null && matchInfo.RecordUserNickName != null && victim != null)
                {
                    string cleanInstigator = RemoveClanTag(instigator);
                    string cleanVictim = RemoveClanTag(victim);
                    string cleanRecordName = RemoveClanTag(matchInfo.RecordUserNickName);

                    if (string.Equals(cleanInstigator, cleanRecordName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(cleanVictim, cleanRecordName, StringComparison.OrdinalIgnoreCase))
                    {
                        var downTime = MatchTimestampToLocal(matchInfo.Timestamp, details.Time);
                        trackedVictims.Add(victim);

                        var bookmark = new Bookmark
                        {
                            Type = BookmarkType.Kill,
                            Time = downTime - Settings.Instance.State.Recording?.StartTime ?? TimeSpan.Zero
                        };
                        Settings.Instance.State.Recording?.Bookmarks.Add(bookmark);
                    }
                }
            }
        }

        private void ProcessKills(string folder, PubgMatchInfo matchInfo, HashSet<string> trackedVictims)
        {
            var killFiles = Directory.GetFiles(Path.Combine(folder, "events"), "kill*");
            foreach (var filePath in killFiles)
            {
                var eventJson = ReadJson(filePath);
                var details = JsonSerializer.Deserialize<PubgEventDetails>(eventJson);

                if (details is null || details.Data is null)
                {
                    Log.Warning("Failed to parse event details from {FilePath}", filePath);
                    continue;
                }

                var rawData = DecodeBase64(details.Data);
                var dataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(rawData);

                if (dataDict is null)
                {
                    Log.Warning("Failed to parse event data from {FilePath}", filePath);
                    continue;
                }

                var dataList = dataDict.Values.ToList();

                var killer = dataList[1]?.ToString();
                var victim = dataList[3]?.ToString();

                if (killer != null && matchInfo.RecordUserNickName != null && victim != null)
                {
                    string cleanKiller = RemoveClanTag(killer);
                    string cleanVictim = RemoveClanTag(victim);
                    string cleanRecordName = RemoveClanTag(matchInfo.RecordUserNickName);

                    if (string.Equals(cleanKiller, cleanRecordName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(cleanVictim, cleanRecordName, StringComparison.OrdinalIgnoreCase))
                    {
                        var killTime = MatchTimestampToLocal(matchInfo.Timestamp, details.Time);
                        bool wasInstantKill = trackedVictims.Add(victim);
                        if (wasInstantKill)
                        {
                            var bookmark = new Bookmark
                            {
                                Type = BookmarkType.Kill,
                                Time = killTime - Settings.Instance.State.Recording?.StartTime ?? TimeSpan.Zero
                            };
                            Settings.Instance.State.Recording?.Bookmarks.Add(bookmark);
                        }
                    }
                }
            }
        }

        private void ProcessPlayerDeath(string folder, PubgMatchInfo matchInfo)
        {
            var killFiles = Directory.GetFiles(Path.Combine(folder, "events"), "kill*");
            foreach (var filePath in killFiles)
            {
                var eventJson = ReadJson(filePath);
                var details = JsonSerializer.Deserialize<PubgEventDetails>(eventJson);

                if (details is null || details.Data is null)
                {
                    Log.Warning("Failed to parse event details from {FilePath}", filePath);
                    continue;
                }

                var rawData = DecodeBase64(details.Data);
                var dataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(rawData);

                if (dataDict is null)
                {
                    Log.Warning("Failed to parse event data from {FilePath}", filePath);
                    continue;
                }

                var dataList = dataDict.Values.ToList();
                var victim = dataList[3]?.ToString();

                // Handle clan tags by removing text within brackets before comparing
                if (victim != null && matchInfo.RecordUserNickName != null)
                {
                    string cleanVictim = RemoveClanTag(victim);
                    string cleanRecordName = RemoveClanTag(matchInfo.RecordUserNickName);

                    if (string.Equals(cleanVictim, cleanRecordName, StringComparison.OrdinalIgnoreCase))
                    {
                        var deathTime = MatchTimestampToLocal(matchInfo.Timestamp, details.Time);
                        var bookmark = new Bookmark
                        {
                            Type = BookmarkType.Death,
                            Time = deathTime - Settings.Instance.State.Recording?.StartTime ?? TimeSpan.Zero
                        };
                        Settings.Instance.State.Recording?.Bookmarks.Add(bookmark);
                    }
                }
            }
        }

        private static string ReadJson(string path)
        {
            var content = File.ReadAllText(path);
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}') + 1;
            return content.Substring(start, end - start);
        }

        private static string DecodeBase64(string base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64));

        private static string RemoveClanTag(string playerName)
        {
            string result = ClanRegex().Replace(playerName, "").Trim();
            return result;
        }

        private static DateTime MatchTimestampToLocal(long matchStart, int offsetMs)
        {
            var utcTime = DateTimeOffset.FromUnixTimeMilliseconds(matchStart + offsetMs).DateTime;
            return TimeZoneInfo.ConvertTimeFromUtc(utcTime, TimeZoneInfo.Local);
        }

        [System.Text.RegularExpressions.GeneratedRegex(@"\[.*?\]")]
        private static partial System.Text.RegularExpressions.Regex ClanRegex();

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}
