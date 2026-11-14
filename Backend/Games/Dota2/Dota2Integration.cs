using Serilog;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Segra.Backend.App;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.Dota2
{
    internal class Dota2Integration : Integration, IDisposable
    {
        private readonly HttpListener _listener = new();
        private const string Prefix = "http://127.0.0.1:1341/";
        private int _lastValidKills = 0;
        private int _lastValidDeaths = 0;
        private int _lastValidAssists = 0;
        private bool _hadAegis = false;
        private bool _initialStatsCaptured = false;
        private readonly object _stateLock = new();
        private readonly MatchTracker _matches = new();
        private string? _matchId;

        private const string PreGame = "DOTA_GAMERULES_STATE_PRE_GAME";
        private const string InProgress = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS";
        private const string PostGame = "DOTA_GAMERULES_STATE_POST_GAME";

        // Internal names that don't read as the hero's name
        private static readonly Dictionary<string, string> HeroNames = new()
        {
            ["abyssal_underlord"] = "Underlord",
            ["antimage"] = "Anti-Mage",
            ["centaur"] = "Centaur Warrunner",
            ["doom_bringer"] = "Doom",
            ["furion"] = "Nature's Prophet",
            ["keeper_of_the_light"] = "Keeper of the Light",
            ["life_stealer"] = "Lifestealer",
            ["magnataur"] = "Magnus",
            ["necrolyte"] = "Necrophos",
            ["nevermore"] = "Shadow Fiend",
            ["obsidian_destroyer"] = "Outworld Destroyer",
            ["queenofpain"] = "Queen of Pain",
            ["rattletrap"] = "Clockwerk",
            ["shredder"] = "Timbersaw",
            ["skeleton_king"] = "Wraith King",
            ["treant"] = "Treant Protector",
            ["vengefulspirit"] = "Vengeful Spirit",
            ["windrunner"] = "Windranger",
            ["wisp"] = "Io",
            ["zuus"] = "Zeus",
        };

        private class GameState
        {
            [System.Text.Json.Serialization.JsonPropertyName("player")]
            public Player? Player { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("hero")]
            public Hero? Hero { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("map")]
            public Map? Map { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("items")]
            public JsonElement Items { get; set; }
        }

        private class Player
        {
            [System.Text.Json.Serialization.JsonPropertyName("steamid")]
            public string? SteamId { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("kills")]
            public int? Kills { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("deaths")]
            public int? Deaths { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("assists")]
            public int? Assists { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("team_name")]
            public string? TeamName { get; set; }
        }

        private class Hero
        {
            [System.Text.Json.Serialization.JsonPropertyName("alive")]
            public bool? Alive { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string? Name { get; set; }
        }

        private class Map
        {
            [System.Text.Json.Serialization.JsonPropertyName("game_state")]
            public string? GameState { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("matchid")]
            public JsonElement MatchId { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("win_team")]
            public string? WinTeam { get; set; }
        }

        public override async Task Start()
        {
            try
            {
                EnsureCfgExists();
                InitializeListener();
                Log.Information($"Dota 2 integration listening on {Prefix}");
                while (_listener.IsListening)
                {
                    try
                    {
                        HttpListenerContext context = await _listener.GetContextAsync();

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await HandleRequest(context);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"Error handling Dota 2 request: {ex.Message}");
                            }
                        });
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
                    {
                        Log.Information("Dota 2 integration listener stopped");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Dota 2 integration error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to start Dota 2 integration: {ex.Message}");
            }
        }

        public override Task Shutdown()
        {
            Log.Information("Shutting down Dota 2 integration");
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception ex)
            {
                Log.Warning($"Error shutting down Dota 2 integration: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Shutdown().Wait();
        }

        private void InitializeListener()
        {
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            try
            {
                if (context.Request.HttpMethod == "POST")
                {
                    string body = await ReadRequestBodyAsync(context.Request);
                    Log.Debug($"Dota 2 integration received payload: {body.Length} bytes");

                    GameState state = DeserializeState(body);
                    ProcessGameState(state);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Error handling Dota 2 request: {ex.Message}");
            }
            finally
            {
                byte[] buffer = Encoding.UTF8.GetBytes("");
                context.Response.ContentLength64 = buffer.Length;
                using (Stream output = context.Response.OutputStream)
                {
                    await output.WriteAsync(buffer, 0, buffer.Length);
                }
                context.Response.Close();
            }
        }

        private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        private GameState DeserializeState(string body)
        {
            try
            {
                return JsonSerializer.Deserialize<GameState>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new GameState();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to deserialize Dota 2 state");
                return new GameState();
            }
        }

        private void ProcessGameState(GameState state)
        {
            lock (_stateLock)
            {
                TrackMatch(state);
                ProcessStatChanges(state);
            }
        }

        private void TrackMatch(GameState state)
        {
            try
            {
                var map = state.Map;
                string? team = state.Player?.TeamName;
                // Spectating or watching a replay has no team of our own
                if (map == null || team is not ("radiant" or "dire"))
                {
                    _matches.End();
                    return;
                }

                string id = map.MatchId.ValueKind switch
                {
                    JsonValueKind.String => map.MatchId.GetString() ?? string.Empty,
                    JsonValueKind.Number => map.MatchId.GetRawText(),
                    _ => string.Empty,
                };
                if (id != _matchId)
                {
                    _matches.End();
                    _matchId = null;
                }

                string? phase = map.GameState;
                if (phase is PreGame or InProgress && !_matches.IsRunning && !(id == _matchId && _matches.Resume()))
                {
                    _matches.Start();
                    _matchId = id;
                }

                if (_matchId == null)
                {
                    return;
                }

                void Apply(GameMatch m)
                {
                    m.Character = FormatHero(state.Hero?.Name) ?? m.Character;
                    m.Kills = state.Player!.Kills ?? m.Kills;
                    m.Deaths = state.Player.Deaths ?? m.Deaths;
                    m.Assists = state.Player.Assists ?? m.Assists;
                }

                if (phase == PostGame)
                {
                    _matches.End(m =>
                    {
                        Apply(m);
                        if (map.WinTeam is "radiant" or "dire")
                            m.Result = map.WinTeam == team ? MatchResult.Win : MatchResult.Loss;
                    });
                }
                else if (_matches.IsRunning)
                {
                    _matches.Update(Apply);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Error tracking the Dota 2 match: {ex.Message}");
            }
        }

        // npc_dota_hero_dragon_knight -> Dragon Knight
        private static string? FormatHero(string? name)
        {
            const string prefix = "npc_dota_hero_";
            if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length)
                return null;

            string hero = name[prefix.Length..];
            return HeroNames.TryGetValue(hero, out string? display)
                ? display
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(hero.Replace('_', ' '));
        }

        private void ProcessStatChanges(GameState state)
        {
            try
            {
                // Only process during live game states (skip pre-game lobby/menu)
                var phase = state.Map?.GameState;
                if (phase != "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS" &&
                    phase != "DOTA_GAMERULES_STATE_POST_GAME")
                {
                    return;
                }

                if (state.Player == null)
                {
                    return;
                }

                var currentKills = state.Player.Kills ?? 0;
                var currentDeaths = state.Player.Deaths ?? 0;
                var currentAssists = state.Player.Assists ?? 0;

                if (!_initialStatsCaptured)
                {
                    Log.Information($"Initial Dota 2 stats captured: K:{currentKills} D:{currentDeaths} A:{currentAssists}");
                    _lastValidKills = currentKills;
                    _lastValidDeaths = currentDeaths;
                    _lastValidAssists = currentAssists;
                    _hadAegis = HasAegis(state.Items);
                    _initialStatsCaptured = true;
                    return;
                }

                if (currentKills < _lastValidKills || currentDeaths < _lastValidDeaths || currentAssists < _lastValidAssists)
                {
                    Log.Information("Dota 2: new game detected, resetting stats");
                    _lastValidKills = currentKills;
                    _lastValidDeaths = currentDeaths;
                    _lastValidAssists = currentAssists;
                    _hadAegis = false;
                    return;
                }

                if (currentKills > _lastValidKills)
                {
                    int delta = currentKills - _lastValidKills;
                    Log.Information($"Dota 2 kill: {_lastValidKills} -> {currentKills} (+{delta})");
                    for (int i = 0; i < delta; i++) AddBookmark(BookmarkType.Kill);
                }

                if (currentDeaths > _lastValidDeaths)
                {
                    int delta = currentDeaths - _lastValidDeaths;
                    Log.Information($"Dota 2 death: {_lastValidDeaths} -> {currentDeaths} (+{delta})");
                    for (int i = 0; i < delta; i++) AddBookmark(BookmarkType.Death);
                }

                if (currentAssists > _lastValidAssists)
                {
                    int delta = currentAssists - _lastValidAssists;
                    Log.Information($"Dota 2 assist: {_lastValidAssists} -> {currentAssists} (+{delta})");
                    for (int i = 0; i < delta; i++) AddBookmark(BookmarkType.Assist);
                }

                _lastValidKills = currentKills;
                _lastValidDeaths = currentDeaths;
                _lastValidAssists = currentAssists;

                bool hasAegis = HasAegis(state.Items);
                if (hasAegis && !_hadAegis)
                {
                    Log.Information("Dota 2: Aegis picked up");
                    AddBookmark(BookmarkType.Kill); // Bookmark big moment as kill (no Pickup type)
                }
                _hadAegis = hasAegis;
            }
            catch (Exception ex)
            {
                Log.Warning($"Error processing Dota 2 state: {ex.Message}");
            }
        }

        private static bool HasAegis(JsonElement items)
        {
            if (items.ValueKind != JsonValueKind.Object) return false;

            foreach (var slot in items.EnumerateObject())
            {
                if (slot.Value.ValueKind != JsonValueKind.Object) continue;
                if (slot.Value.TryGetProperty("name", out var nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String &&
                    string.Equals(nameEl.GetString(), "item_aegis", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void AddBookmark(BookmarkType type)
        {
            var recording = AppState.Instance.Recording;
            if (recording == null)
            {
                Log.Debug($"No recording active, skipping {type} bookmark");
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

        private void EnsureCfgExists()
        {
            try
            {
                string? cfgDir = ResolveCfgDirectory(ExePath);
                if (cfgDir == null)
                {
                    Log.Warning($"Could not derive Dota 2 cfg directory from exe path '{ExePath}'. Skipping cfg write.");
                    return;
                }

                string cfgPath = Path.Combine(cfgDir, "gamestate_integration_segra.cfg");
                string expectedContent = GenerateCfg();

                if (File.Exists(cfgPath))
                {
                    string existingContent = File.ReadAllText(cfgPath);
                    if (existingContent.Equals(expectedContent, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                Directory.CreateDirectory(cfgDir);
                File.WriteAllText(cfgPath, expectedContent);
                Log.Information($"Created Dota 2 gamestate integration config at {cfgPath}");
                _ = MessageService.ShowModal("Game integration", $"There has been an update to the Dota 2 integration. Please restart the game to apply the changes.", "warning");
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not ensure Dota 2 cfg exists: {ex.Message}");
            }
        }

        private static string? ResolveCfgDirectory(string? exePath)
        {
            // dota2.exe lives at: <install>\dota 2 beta\game\bin\win64\dota2.exe
            // The cfg dir is:     <install>\dota 2 beta\game\dota\cfg
            if (string.IsNullOrEmpty(exePath)) return null;

            string? winDir = Path.GetDirectoryName(exePath);          // ...\game\bin\win64
            string? binDir = Path.GetDirectoryName(winDir);            // ...\game\bin
            string? gameDir = Path.GetDirectoryName(binDir);           // ...\game
            if (string.IsNullOrEmpty(gameDir)) return null;

            return Path.Combine(gameDir, "dota", "cfg", "gamestate_integration");
        }

        private static string GenerateCfg()
        {
            return "\"Segra\"\n" +
                "{\n" +
                "    \"uri\"           \"http://localhost:1341/\"\n" +
                "    \"timeout\"       \"5.0\"\n" +
                "    \"buffer\"        \"0.1\"\n" +
                "    \"throttle\"      \"0.1\"\n" +
                "    \"heartbeat\"     \"30.0\"\n" +
                "    \"data\"\n" +
                "    {\n" +
                "        \"provider\"      \"1\"\n" +
                "        \"map\"           \"1\"\n" +
                "        \"player\"        \"1\"\n" +
                "        \"hero\"          \"1\"\n" +
                "        \"items\"         \"1\"\n" +
                "    }\n" +
                "}\n";
        }
    }
}
