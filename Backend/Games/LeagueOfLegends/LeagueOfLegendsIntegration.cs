using Segra.Backend.Core.Models;
using Serilog;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Segra.Backend.Games.LeagueOfLegends
{
    internal class LeagueOfLegendsIntegration : Integration, IDisposable
    {
        private static System.Timers.Timer? _timer;
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
            return Task.CompletedTask;
        }

        private async Task PollGameData()
        {
            try
            {
                string result = await _client.GetStringAsync($"{_liveClientDataUrl}/allgamedata");
                JsonDocument doc = JsonDocument.Parse(result);
                JsonElement root = doc.RootElement;

                // Check if game has started
                if (!IsGameStarted(root))
                {
                    return;
                }

                // If we reach here, the game is in progress
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

                // Find current player data
                JsonElement currentPlayer = FindCurrentPlayer(root, summonerName);
                if (currentPlayer.ValueKind == JsonValueKind.Undefined)
                {
                    return;
                }

                // Process player stats
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

            // First capture the current stats
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

            // Process kills
            if (currentKills > _stats.Kills)
            {
                Log.Information($"Player got a kill! Total: {currentKills}");
                AddBookmark(BookmarkType.Kill);
                _stats.Kills = currentKills;
            }

            // Process deaths
            if (currentDeaths > _stats.Deaths)
            {
                Log.Information($"Player died! Total deaths: {currentDeaths}");
                AddBookmark(BookmarkType.Death);
                _stats.Deaths = currentDeaths;
            }

            // Process assists
            if (currentAssists > _stats.Assists)
            {
                Log.Information($"Player got an assist! Total: {currentAssists}");
                AddBookmark(BookmarkType.Assist);
                _stats.Assists = currentAssists;
            }

            // Process champion name if available
            if (currentPlayer.TryGetProperty("championName", out JsonElement championElement))
            {
                _stats.Champion = championElement.GetString() ?? "";
            }
            else if (currentPlayer.TryGetProperty("rawChampionName", out JsonElement rawChampionElement))
            {
                string rawName = rawChampionElement.GetString() ?? "";
                if (!string.IsNullOrEmpty(rawName))
                {
                    _stats.Champion = Regex.Replace(rawName, "game_character_displayname_", "");
                    if (_stats.Champion == "FiddleSticks")
                    {
                        _stats.Champion = "Fiddlesticks";
                    }
                }
            }
        }

        private void AddBookmark(BookmarkType type)
        {
            if (Settings.Instance.State.Recording == null)
            {
                return;
            }

            var bookmark = new Bookmark
            {
                Type = type,
                Time = DateTime.Now - Settings.Instance.State.Recording.StartTime
            };

            Settings.Instance.State.Recording.Bookmarks.Add(bookmark);
            Log.Information($"Added {type} bookmark at {bookmark.Time}");
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }

    public class PlayerStats
    {
        public int Kills { get; set; } = 0;
        public int Deaths { get; set; } = 0;
        public int Assists { get; set; } = 0;
        public string Champion { get; set; } = string.Empty;
        public bool? Win { get; set; } = null;
    }

    // Classes to deserialize the League of Legends Live Client Data API responses
    public class LeagueGameData
    {
        [JsonPropertyName("activePlayer")]
        public ActivePlayer ActivePlayer { get; set; } = new();

        [JsonPropertyName("allPlayers")]
        public List<Player> AllPlayers { get; set; } = new();

        [JsonPropertyName("events")]
        public Events Events { get; set; } = new();

        [JsonPropertyName("gameData")]
        public GameData GameData { get; set; } = new();
    }

    public class ActivePlayer
    {
        [JsonPropertyName("summonerName")]
        public string SummonerName { get; set; } = string.Empty;
    }

    public class Player
    {
        [JsonPropertyName("championName")]
        public string ChampionName { get; set; } = string.Empty;

        [JsonPropertyName("rawChampionName")]
        public string RawChampionName { get; set; } = string.Empty;

        [JsonPropertyName("summonerName")]
        public string SummonerName { get; set; } = string.Empty;

        [JsonPropertyName("scores")]
        public Scores Scores { get; set; } = new();

        [JsonPropertyName("team")]
        public string Team { get; set; } = string.Empty;
    }

    public class Scores
    {
        [JsonPropertyName("assists")]
        public int Assists { get; set; }

        [JsonPropertyName("deaths")]
        public int Deaths { get; set; }

        [JsonPropertyName("kills")]
        public int Kills { get; set; }
    }

    public class Events
    {
        [JsonPropertyName("Events")]
        public List<Event> EventsList { get; set; } = new();
    }

    public class Event
    {
        [JsonPropertyName("EventID")]
        public int EventId { get; set; }

        [JsonPropertyName("EventName")]
        public string EventName { get; set; } = string.Empty;

        [JsonPropertyName("EventTime")]
        public double EventTime { get; set; }

        [JsonPropertyName("Result")]
        public string Result { get; set; } = string.Empty;
    }

    public class GameData
    {
        [JsonPropertyName("gameMode")]
        public string GameMode { get; set; } = string.Empty;

        [JsonPropertyName("gameTime")]
        public double GameTime { get; set; }
    }
}
