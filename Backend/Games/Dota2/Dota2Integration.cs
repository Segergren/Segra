using Segra.Backend.Core.Models;
using Serilog;
using System.Net;
using System.Text;
using System.Text.Json;

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
        }

        private class Hero
        {
            [System.Text.Json.Serialization.JsonPropertyName("alive")]
            public bool? Alive { get; set; }
        }

        private class Map
        {
            [System.Text.Json.Serialization.JsonPropertyName("game_state")]
            public string? GameState { get; set; }
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
            if (AppState.Instance.Recording == null)
            {
                Log.Debug($"No recording active, skipping {type} bookmark");
                return;
            }

            var bookmark = new Bookmark
            {
                Type = type,
                Time = DateTime.Now - AppState.Instance.Recording.StartTime
            };
            AppState.Instance.Recording.Bookmarks.Add(bookmark);
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
                _ = Segra.Backend.App.MessageService.ShowModal("Game integration", $"There has been an update to the Dota 2 integration. Please restart the game to apply the changes.", "warning");
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

        public void Dispose()
        {
            Shutdown().Wait();
        }
    }
}
