using Serilog;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.RainbowSixSiege
{
    internal class RainbowSixSiegeIntegration : Integration
    {
        private const string DissectUrl = "https://cdn.segra.tv/r6-dissect/r6-dissect.exe";
        private const int PrepPhaseSeconds = 45;
        private const int ActionPhaseSeconds = 180;
        private const int DefuserSeconds = 45;
        private const int PlantDurationSeconds = 7;

        private static readonly string DissectPath = Path.Combine(Settings.Instance.CacheFolder, "r6-dissect", "r6-dissect.exe");

        private readonly System.Timers.Timer checkTimer = new(2500);
        private readonly HashSet<string> processedFiles = [];
        private string? replayFolder;
        private DateTime startedAt;

        private class Round
        {
            [JsonPropertyName("timestamp")]
            public string? Timestamp { get; set; }
            [JsonPropertyName("roundNumber")]
            public int RoundNumber { get; set; }
            [JsonPropertyName("recordingPlayerID")]
            public ulong RecordingPlayerId { get; set; }
            [JsonPropertyName("players")]
            public List<Player> Players { get; set; } = [];
            [JsonPropertyName("matchFeedback")]
            public List<MatchUpdate> MatchFeedback { get; set; } = [];
        }

        private class Player
        {
            [JsonPropertyName("id")]
            public ulong Id { get; set; }
            [JsonPropertyName("username")]
            public string? Username { get; set; }
        }

        private class MatchUpdate
        {
            [JsonPropertyName("type")]
            public MatchUpdateType? Type { get; set; }
            [JsonPropertyName("username")]
            public string? Username { get; set; }
            [JsonPropertyName("target")]
            public string? Target { get; set; }
            [JsonPropertyName("headshot")]
            public bool? Headshot { get; set; }
            [JsonPropertyName("timeInSeconds")]
            public double TimeInSeconds { get; set; }
        }

        private class MatchUpdateType
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }
        }

        public RainbowSixSiegeIntegration()
        {
            checkTimer.Elapsed += (_, _) => TimerTick();
        }

        public override async Task Start()
        {
            var gameFolder = Path.GetDirectoryName(ExePath);
            if (gameFolder == null)
            {
                Log.Warning("Rainbow Six Siege integration needs the game path to find match replays");
                return;
            }

            replayFolder = Path.Combine(gameFolder, "MatchReplay");
            startedAt = DateTime.Now;

            if (!await EnsureDissect())
                return;

            Log.Information("Initializing Rainbow Six Siege replay integration.");
            checkTimer.Start();
        }

        public override Task Shutdown()
        {
            Log.Information("Stopping Rainbow Six Siege replay integration.");
            checkTimer.Stop();
            return Task.CompletedTask;
        }

        private static async Task<bool> EnsureDissect()
        {
            if (File.Exists(DissectPath))
                return true;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DissectPath)!);
                using var httpClient = new HttpClient();
                var bytes = await httpClient.GetByteArrayAsync(DissectUrl);
                var downloadPath = DissectPath + ".download";
                await File.WriteAllBytesAsync(downloadPath, bytes);
                File.Move(downloadPath, DissectPath, true);
                Log.Information($"Downloaded r6-dissect to {DissectPath}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to download r6-dissect");
                return false;
            }
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
            var round = RunDissect(file);
            if (round?.Timestamp == null)
                return;

            var me = round.Players.FirstOrDefault(p => p.Id == round.RecordingPlayerId)?.Username;
            var recording = AppState.Instance.Recording;
            if (me == null || recording == null)
                return;

            var actionStart = DateTime.ParseExact(round.Timestamp.TrimEnd('Z'), "s", CultureInfo.InvariantCulture)
                .AddSeconds(PrepPhaseSeconds);
            DateTime? plantStart = null;
            DateTime? planted = null;
            var afterPlant = false;
            var added = 0;

            foreach (var update in round.MatchFeedback)
            {
                if (afterPlant && planted == null)
                    break;

                var time = afterPlant
                    ? planted!.Value.AddSeconds(DefuserSeconds - update.TimeInSeconds)
                    : actionStart.AddSeconds(ActionPhaseSeconds - update.TimeInSeconds);

                switch (update.Type?.Name)
                {
                    case "DefuserPlantStart":
                        plantStart = time;
                        break;
                    case "DefuserPlantComplete":
                        planted = plantStart?.AddSeconds(PlantDurationSeconds);
                        afterPlant = true;
                        break;
                    case "Kill" when update.Username == me && update.Target != me:
                        added += AddBookmark(recording, BookmarkType.Kill, update.Headshot == true ? BookmarkSubtype.Headshot : null, time);
                        break;
                    case "Kill" when update.Target == me:
                    case "Death" when update.Username == me:
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

        private static Round? RunDissect(string file)
        {
            var startInfo = new ProcessStartInfo(DissectPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(file);

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000))
            {
                process.Kill();
                Log.Warning($"r6-dissect timed out on {file}");
                return null;
            }

            if (process.ExitCode != 0)
            {
                Log.Warning($"r6-dissect failed on {file}: {stderr.Result.Trim()}");
                return null;
            }

            return JsonSerializer.Deserialize<Round>(stdout.Result);
        }
    }
}
