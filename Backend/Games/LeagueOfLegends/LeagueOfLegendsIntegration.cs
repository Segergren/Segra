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
        private bool _matchStarted = false;
        private readonly MatchTracker _matches = new();
        private readonly object _matchLock = new();
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
                    _matchStarted = false;
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
                // Polls can overlap when a response is slow
                lock (_matchLock)
                {
                    TrackMatch(root, currentPlayer);
                }
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

        private bool IsGameStarted(JsonElement root) => FindEvent(root, "GameStart") != null;

        private static JsonElement? FindEvent(JsonElement root, string name)
        {
            if (!root.TryGetProperty("events", out JsonElement eventList) ||
                !eventList.TryGetProperty("Events", out JsonElement events) ||
                events.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement element in events.EnumerateArray())
            {
                if (element.TryGetProperty("EventName", out JsonElement propertyValue) &&
                    propertyValue.ValueKind == JsonValueKind.String &&
                    propertyValue.GetString() == name)
                {
                    return element;
                }
            }
            return null;
        }

        private void TrackMatch(JsonElement root, JsonElement currentPlayer)
        {
            root.TryGetProperty("gameData", out JsonElement gameData);

            if (!_matchStarted)
            {
                _matchStarted = true;
                // The game clock starts at GameStart, so it tells how long ago the match began
                double gameTime = gameData.ValueKind == JsonValueKind.Object &&
                    gameData.TryGetProperty("gameTime", out JsonElement time) && time.ValueKind == JsonValueKind.Number
                    ? time.GetDouble()
                    : 0;
                _matches.Start(startedAgo: TimeSpan.FromSeconds(Math.Max(0, gameTime)));
            }

            void Apply(GameMatch m)
            {
                if (gameData.ValueKind == JsonValueKind.Object)
                {
                    if (gameData.TryGetProperty("mapNumber", out JsonElement mapNumber) && mapNumber.ValueKind == JsonValueKind.Number)
                        m.Map = FormatMap(mapNumber.GetInt32());
                    if (gameData.TryGetProperty("gameMode", out JsonElement mode) && mode.ValueKind == JsonValueKind.String)
                        m.Mode = FormatMode(mode.GetString());
                }
                if (currentPlayer.TryGetProperty("championName", out JsonElement champion) && champion.ValueKind == JsonValueKind.String)
                    m.Character = champion.GetString();
                if (currentPlayer.TryGetProperty("scores", out JsonElement scores))
                {
                    if (scores.TryGetProperty("kills", out JsonElement kills)) m.Kills = kills.GetInt32();
                    if (scores.TryGetProperty("deaths", out JsonElement deaths)) m.Deaths = deaths.GetInt32();
                    if (scores.TryGetProperty("assists", out JsonElement assists)) m.Assists = assists.GetInt32();
                }
            }

            if (FindEvent(root, "GameEnd") is { } gameEnd)
            {
                _matches.End(m =>
                {
                    Apply(m);
                    string? result = gameEnd.TryGetProperty("Result", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    m.Result = result switch
                    {
                        "Win" => MatchResult.Win,
                        "Lose" => MatchResult.Loss,
                        _ => null,
                    };
                }, finished: true);
            }
            else if (_matches.IsRunning)
            {
                _matches.Update(Apply);
            }
        }

        private static string? FormatMap(int mapNumber) => mapNumber switch
        {
            11 => "Summoner's Rift",
            12 => "Howling Abyss",
            21 => "Nexus Blitz",
            30 => "Rings of Wrath",
            _ => null,
        };

        private static string? FormatMode(string? mode) => mode switch
        {
            null or "" or "CLASSIC" => null,
            "ARAM" or "URF" or "ARURF" => mode,
            "CHERRY" => "Arena",
            "ONEFORALL" => "One for All",
            "NEXUSBLITZ" => "Nexus Blitz",
            "PRACTICETOOL" => "Practice Tool",
            "TUTORIAL" => "Tutorial",
            "ULTBOOK" => "Ultimate Spellbook",
            "STRAWBERRY" => "Swarm",
            _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(mode.ToLowerInvariant()),
        };

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
