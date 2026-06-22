using Serilog;
using System.Text.Json;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.LeagueOfLegends
{
    internal class LeagueOfLegendsIntegration : Integration, IDisposable
    {
        private System.Timers.Timer? _timer;
        private readonly HttpClientHandler _handler;
        private readonly HttpClient _client;
        private PlayerStats _stats;
        private bool _isGameInProgress = false;
        private bool _initialStatsCaptured = false;
        private readonly string _liveClientDataUrl = "https://127.0.0.1:2999/liveclientdata";

        public LeagueOfLegendsIntegration()
        {
            _handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            _client = new HttpClient(_handler);
            _stats = new PlayerStats();
            _timer = new System.Timers.Timer
            {
                Interval = 250
            };
            _timer.Elapsed += async (sender, e) => await PollGameData();
        }

        public override Task Start()
        {
            Log.Information("Initializing League of Legends data integration.");
            _timer?.Start();
            return Task.CompletedTask;
        }

        public override Task Shutdown()
        {
            if (_timer != null && _timer.Enabled)
            {
                _timer.Stop();
                _timer.Dispose();
                Log.Information("Stopping League of Legends data integration.");
            }
            _client.Dispose();
            return Task.CompletedTask;
        }

        public void Dispose() => Shutdown().Wait();

        private async Task PollGameData()
        {
            try
            {
                string result = await _client.GetStringAsync($"{_liveClientDataUrl}/allgamedata");
                JsonDocument doc = JsonDocument.Parse(result);
                JsonElement root = doc.RootElement;

                if (!IsGameStarted(root))
                {
                    return;
                }

                if (!_isGameInProgress)
                {
                    Log.Information("League of Legends game detected and started");
                    _isGameInProgress = true;
                    _initialStatsCaptured = false;
                }

                string summonerName = GetSummonerName(root);
                if (string.IsNullOrEmpty(summonerName))
                {
                    return;
                }

                JsonElement currentPlayer = FindCurrentPlayer(root, summonerName);
                if (currentPlayer.ValueKind == JsonValueKind.Undefined)
                {
                    return;
                }

                ProcessPlayerStats(currentPlayer);
            }
            catch (HttpRequestException)
            {
                // Game client is not running or not in game, this is expected
                if (_isGameInProgress)
                {
                    _isGameInProgress = false;
                    Log.Information("League of Legends game ended or client closed");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"League of Legends integration error: {ex.Message}");
            }
        }

        private bool IsGameStarted(JsonElement root)
        {
            if (!root.TryGetProperty("events", out JsonElement eventList))
            {
                return false;
            }

            if (!eventList.TryGetProperty("Events", out JsonElement events))
            {
                return false;
            }

            return events.EnumerateArray().Any(
                element => element.TryGetProperty("EventName", out JsonElement propertyValue) &&
                propertyValue.GetString() == "GameStart");
        }

        private string GetSummonerName(JsonElement root)
        {
            string summonerName = "";

            // Try to get riotId first (newer API)
            if (root.TryGetProperty("activePlayer", out JsonElement activePlayer) &&
                activePlayer.TryGetProperty("riotId", out JsonElement id))
            {
                summonerName = id.GetString() ?? "";
            }
            // Fall back to summonerName (older API)
            else if (root.TryGetProperty("activePlayer", out activePlayer) &&
                activePlayer.TryGetProperty("summonerName", out id))
            {
                summonerName = id.GetString() ?? "";
            }

            return summonerName;
        }

        private JsonElement FindCurrentPlayer(JsonElement root, string summonerName)
        {
            if (!root.TryGetProperty("allPlayers", out JsonElement allPlayers))
            {
                return default;
            }

            foreach (JsonElement player in allPlayers.EnumerateArray())
            {
                // Try riotId first (newer API)
                if (player.TryGetProperty("riotId", out JsonElement id) &&
                    id.GetString() == summonerName)
                {
                    return player;
                }
                // Fall back to summonerName (older API)
                else if (player.TryGetProperty("summonerName", out id) &&
                    id.GetString() == summonerName)
                {
                    return player;
                }
            }

            return default;
        }

        private void ProcessPlayerStats(JsonElement currentPlayer)
        {
            if (!currentPlayer.TryGetProperty("scores", out JsonElement scores))
            {
                return;
            }

            int currentKills = 0;
            int currentDeaths = 0;
            int currentAssists = 0;

            if (scores.TryGetProperty("kills", out JsonElement killsElement))
            {
                currentKills = killsElement.GetInt32();
            }

            if (scores.TryGetProperty("deaths", out JsonElement deathsElement))
            {
                currentDeaths = deathsElement.GetInt32();
            }

            if (scores.TryGetProperty("assists", out JsonElement assistsElement))
            {
                currentAssists = assistsElement.GetInt32();
            }

            // If this is the first time we're capturing stats for this game session,
            // just store the values without creating bookmarks
            if (!_initialStatsCaptured)
            {
                Log.Information($"Initial League stats captured: K:{currentKills} D:{currentDeaths} A:{currentAssists}");
                _stats.Kills = currentKills;
                _stats.Deaths = currentDeaths;
                _stats.Assists = currentAssists;
                _initialStatsCaptured = true;
                return;
            }

            if (currentKills > _stats.Kills)
            {
                Log.Information($"Player got a kill! Total: {currentKills}");
                AddBookmark(BookmarkType.Kill);
                _stats.Kills = currentKills;
            }

            if (currentDeaths > _stats.Deaths)
            {
                Log.Information($"Player died! Total deaths: {currentDeaths}");
                AddBookmark(BookmarkType.Death);
                _stats.Deaths = currentDeaths;
            }

            if (currentAssists > _stats.Assists)
            {
                Log.Information($"Player got an assist! Total: {currentAssists}");
                AddBookmark(BookmarkType.Assist);
                _stats.Assists = currentAssists;
            }
        }

        private void AddBookmark(BookmarkType type)
        {
            var recording = AppState.Instance.Recording;
            if (recording == null)
            {
                return;
            }

            var bookmark = new Bookmark
            {
                Type = type,
                Time = DateTime.Now - recording.StartTime
            };

            recording.AddBookmark(bookmark);
            Log.Information($"Added {type} bookmark at {bookmark.Time}");
        }
    }

    public class PlayerStats
    {
        public int Kills { get; set; } = 0;
        public int Deaths { get; set; } = 0;
        public int Assists { get; set; } = 0;
    }
}
