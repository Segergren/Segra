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

        public class PubgEventData
        {
            [JsonPropertyName("instigatorName")]
            public string? InstigatorName { get; set; }
            [JsonPropertyName("victimName")]
            public string? VictimName { get; set; }
            [JsonPropertyName("damageCauseClassName")]
            public string? DamageCauseClassName { get; set; }
            [JsonPropertyName("damageTypeCategory")]
            public string? DamageTypeCategory { get; set; }
            [JsonPropertyName("bDBNO")]
            public bool IsDBNO { get; set; }
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
                    Thread.Sleep(500);

                    var infoPath = Path.Combine(directory, "PUBG.replayinfo");
                    var matchJson = ReadJsonFromFile(infoPath);
                    var matchInfo = JsonSerializer.Deserialize<PubgMatchInfo>(matchJson);

                    if (matchInfo is null)
                    {
                        Log.Warning("Failed to parse match info from {InfoPath}", infoPath);
                        continue;
                    }

                    ProcessDownedPlayers(directory, matchInfo);
                    ProcessKills(directory, matchInfo);
                    ProcessPlayerDowned(directory, matchInfo);
                    ProcessPlayerDeath(directory, matchInfo);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"PUBG integration encountered an error: {ex.Message}");
            }
        }

        private void ProcessDownedPlayers(string folder, PubgMatchInfo matchInfo)
        {
            var downFiles = Directory.GetFiles(Path.Combine(folder, "events"), "DBNO*");
            foreach (var filePath in downFiles)
            {
                var result = ParseEventData(filePath);
                if (result is null) continue;
                var (eventData, eventTime) = result.Value;

                if (eventData.InstigatorName != null && matchInfo.RecordUserNickName != null && eventData.VictimName != null)
                {
                    string cleanInstigator = RemoveClanTag(eventData.InstigatorName);
                    string cleanVictim = RemoveClanTag(eventData.VictimName);
                    string cleanRecordName = RemoveClanTag(matchInfo.RecordUserNickName);

                    if (string.Equals(cleanInstigator, cleanRecordName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(cleanVictim, cleanRecordName, StringComparison.OrdinalIgnoreCase))
                    {
                        var downTime = MatchTimestampToLocal(matchInfo.Timestamp, eventTime);
                        var bookmarkTime = downTime - Settings.Instance.State.Recording?.StartTime ?? TimeSpan.Zero;

                        // Skip events that occurred before recording started
                        if (bookmarkTime < TimeSpan.Zero)
                            continue;

                        var bookmark = new Bookmark
                        {
                            Type = BookmarkType.Kill,
                            Time = bookmarkTime
                        };
                        Settings.Instance.State.Recording?.Bookmarks.Add(bookmark);
                    }
                }
            }
        }

        private void ProcessKills(string folder, PubgMatchInfo matchInfo)
        {
            var killFiles = Directory.GetFiles(Path.Combine(folder, "events"), "kill*");
            foreach (var filePath in killFiles)
            {
                var result = ParseEventData(filePath);
                if (result is null) continue;
                var (eventData, eventTime) = result.Value;

                if (eventData.InstigatorName != null && matchInfo.RecordUserNickName != null && eventData.VictimName != null)
                {
                    string cleanKiller = RemoveClanTag(eventData.InstigatorName);
                    string cleanVictim = RemoveClanTag(eventData.VictimName);
                    string cleanRecordName = RemoveClanTag(matchInfo.RecordUserNickName);

                    if (string.Equals(cleanKiller, cleanRecordName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(cleanVictim, cleanRecordName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Only bookmark instant kills (IsDBNO=false)
                        // Downs are already bookmarked in ProcessDownedPlayers
                        if (!eventData.IsDBNO)
                        {
                            var killTime = MatchTimestampToLocal(matchInfo.Timestamp, eventTime);
                            var bookmarkTime = killTime - Settings.Instance.State.Recording?.StartTime ?? TimeSpan.Zero;

                            // Skip events that occurred before recording started
                            if (bookmarkTime < TimeSpan.Zero)
                                continue;

                            var bookmark = new Bookmark
                            {
                                Type = BookmarkType.Kill,
                                Time = bookmarkTime
                            };
                            Settings.Instance.State.Recording?.Bookmarks.Add(bookmark);
                        }
                    }
                }
            }
        }

        private void ProcessPlayerDowned(string folder, PubgMatchInfo matchInfo)
        {
            var downFiles = Directory.GetFiles(Path.Combine(folder, "events"), "DBNO*");
            foreach (var filePath in downFiles)
            {
                var result = ParseEventData(filePath);
                if (result is null) continue;
                var (eventData, eventTime) = result.Value;

                if (eventData.VictimName != null && matchInfo.RecordUserNickName != null)
                {
                    string cleanVictim = RemoveClanTag(eventData.VictimName);
                    string cleanRecordName = RemoveClanTag(matchInfo.RecordUserNickName);

                    if (string.Equals(cleanVictim, cleanRecordName, StringComparison.OrdinalIgnoreCase))
                    {
                        var downTime = MatchTimestampToLocal(matchInfo.Timestamp, eventTime);
                        var bookmarkTime = downTime - Settings.Instance.State.Recording?.StartTime ?? TimeSpan.Zero;

                        // Skip events that occurred before recording started
                        if (bookmarkTime < TimeSpan.Zero)
                            continue;

                        var bookmark = new Bookmark
                        {
                            Type = BookmarkType.Death,
                            Time = bookmarkTime
                        };
                        Settings.Instance.State.Recording?.Bookmarks.Add(bookmark);
                    }
                }
            }
        }

        private void ProcessPlayerDeath(string folder, PubgMatchInfo matchInfo)
        {
            var killFiles = Directory.GetFiles(Path.Combine(folder, "events"), "kill*");
            foreach (var filePath in killFiles)
            {
                var result = ParseEventData(filePath);
                if (result is null) continue;
                var (eventData, eventTime) = result.Value;

                if (eventData.VictimName != null && matchInfo.RecordUserNickName != null)
                {
                    string cleanVictim = RemoveClanTag(eventData.VictimName);
                    string cleanRecordName = RemoveClanTag(matchInfo.RecordUserNickName);

                    if (string.Equals(cleanVictim, cleanRecordName, StringComparison.OrdinalIgnoreCase))
                    {
                        // IsDBNO=false means instant kill (no down state before death)
                        // Only add death bookmark for instant kills since downs are handled separately
                        if (!eventData.IsDBNO)
                        {
                            var deathTime = MatchTimestampToLocal(matchInfo.Timestamp, eventTime);
                            var bookmarkTime = deathTime - Settings.Instance.State.Recording?.StartTime ?? TimeSpan.Zero;

                            // Skip events that occurred before recording started
                            if (bookmarkTime < TimeSpan.Zero)
                                continue;

                            var bookmark = new Bookmark
                            {
                                Type = BookmarkType.Death,
                                Time = bookmarkTime
                            };
                            Settings.Instance.State.Recording?.Bookmarks.Add(bookmark);
                        }
                    }
                }
            }
        }

        private static string ReadJsonFromFile(string path)
        {
            var content = File.ReadAllText(path);
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}') + 1;
            return content.Substring(start, end - start);
        }

        private static (PubgEventData Data, int Time)? ParseEventData(string filePath)
        {
            try
            {
                var content = File.ReadAllText(filePath);
                var start = content.IndexOf('{');
                var end = content.LastIndexOf('}') + 1;
                var eventJson = content.Substring(start, end - start);

                var details = JsonSerializer.Deserialize<PubgEventDetails>(eventJson);
                if (details?.Data is null) return null;

                var rawData = Encoding.UTF8.GetString(Convert.FromBase64String(details.Data));
                var data = JsonSerializer.Deserialize<PubgEventData>(rawData);
                if (data is null) return null;

                return (data, details.Time);
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to parse event data from {FilePath}: {Message}", filePath, ex.Message);
                return null;
            }
        }

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
