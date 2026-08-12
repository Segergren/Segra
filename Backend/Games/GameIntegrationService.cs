using Serilog;
using Segra.Backend.Games.Pubg;
using Segra.Backend.Games.Rust;
using Segra.Backend.Core.Models;
using Segra.Backend.Games.Dota2;
using Segra.Backend.Games.Minecraft;
using Segra.Backend.Games.WarThunder;
using Segra.Backend.Games.CounterStrike2;
using Segra.Backend.Games.LeagueOfLegends;
using Segra.Backend.Games.RunescapeDragonwilds;
#if WINDOWS
using Segra.Backend.Games.RocketLeague;
using Segra.Backend.Games.GrandTheftAuto;
#endif
using Segra.Backend.Detection;

namespace Segra.Backend.Games
{
    public static class GameIntegrationService
    {
        private const int PUBG_IGDB_ID = 27789;
        private const int LOL_IGDB_ID = 115;
        private const int CS2_IGDB_ID = 242408;
        private const int ROCKET_LEAGUE_IGDB_ID = 11198;
        private const int DOTA2_IGDB_ID = 2963;
        private const int RUST_IGDB_ID = 3277;
        private const int MINECRAFT_IGDB_ID = 135400;
        private const int RUNESCAPE_DRAGONWILDS_IGDB_ID = 337712;
        private const int WAR_THUNDER_IGDB_ID = 2165;
        private const int OVERWATCH_IGDB_ID = 125174;

        private const int GTA_V_IGDB_ID = 1020;
        private const int FIVEM_IGDB_ID = 146553;
        private const int RAGE_MP_IGDB_ID = 212734;

        // The IGDB id is the stable key: a display name can be null, localized or renamed
        // ("Overwatch 2") and sanitize to an id with no model behind it. Spelled as the directory
        // is on disk — this string also keys ModelService's session cache, so one canonical
        // spelling keeps a name-matched and an id-matched start on the same session.
        private const string OVERWATCH_MODEL_ID = "Overwatch";

        private static readonly Dictionary<int, string> _modelIdsByIgdbId = new()
        {
            [OVERWATCH_IGDB_ID] = OVERWATCH_MODEL_ID,
        };

        private static Integration? _gameIntegration;
        private static readonly SemaphoreSlim _lock = new(1, 1);

        // One unit, one lifetime, swapped in a single assignment. As three mutable statics, a
        // Start or Shutdown could null one out from under a callback already past its null check —
        // on the detector's own inference thread, so the NRE was swallowed as a Log.Warning.
        internal sealed record DetectionSession(
            VisualEventDetector Detector,
            CooldownTracker CooldownTracker,
            IReadOnlyList<EventDefinition> EventDefinitions);

        private static DetectionSession? _detectionSession;

        public static async Task Start(int? igdbId, string? gameName = null, string? exePath = null)
        {
            await _lock.WaitAsync();
            try
            {
                if (_gameIntegration != null)
                {
                    Log.Information("Active game integration already exists! Shutting down before starting");
                    await _gameIntegration.Shutdown();
                    _gameIntegration = null;
                }

                // Every path below can decline to start detection — no game name, no model, toggle
                // off — so the previous session is stopped here, not in the success branch. Left
                // running it appends bookmarks to a recording that now belongs to another game.
                var previousSession = Interlocked.Exchange(ref _detectionSession, null);
                if (previousSession != null)
                {
                    Log.Information("Active visual detector already exists! Stopping before starting");
                    previousSession.Detector.Stop();
                }

                var integrations = Settings.Instance.GameIntegrations;

                if ((igdbId == PUBG_IGDB_ID || gameName?.Contains("PUBG:", StringComparison.OrdinalIgnoreCase) == true || gameName?.Contains("PLAYERUNKNOWN'S BATTLEGROUNDS", StringComparison.OrdinalIgnoreCase) == true) && integrations.Pubg.Enabled)
                    _gameIntegration = new PubgIntegration();
                else if ((igdbId == LOL_IGDB_ID || gameName?.Equals("League of Legends", StringComparison.OrdinalIgnoreCase) == true) && integrations.LeagueOfLegends.Enabled)
                    _gameIntegration = new LeagueOfLegendsIntegration();
                else if ((igdbId == CS2_IGDB_ID || gameName?.Equals("Counter-Strike 2", StringComparison.OrdinalIgnoreCase) == true) && integrations.CounterStrike2.Enabled)
                    _gameIntegration = new CounterStrike2Integration();
#if WINDOWS
                else if ((igdbId == ROCKET_LEAGUE_IGDB_ID || gameName?.Equals("Rocket League", StringComparison.OrdinalIgnoreCase) == true) && integrations.RocketLeague.Enabled)
                    _gameIntegration = new RocketLeagueIntegration();
#endif
                else if ((igdbId == DOTA2_IGDB_ID || gameName?.Equals("Dota 2", StringComparison.OrdinalIgnoreCase) == true) && integrations.Dota2.Enabled)
                    _gameIntegration = new Dota2Integration();
                else if ((igdbId == RUST_IGDB_ID || gameName?.Equals("Rust", StringComparison.OrdinalIgnoreCase) == true) && integrations.Rust.Enabled)
                    _gameIntegration = new RustIntegration();
                else if ((igdbId == MINECRAFT_IGDB_ID || gameName?.Equals("Minecraft", StringComparison.OrdinalIgnoreCase) == true) && integrations.Minecraft.Enabled)
                    _gameIntegration = new MinecraftIntegration();
                else if ((igdbId == RUNESCAPE_DRAGONWILDS_IGDB_ID || gameName?.Contains("Dragonwilds", StringComparison.OrdinalIgnoreCase) == true) && integrations.RunescapeDragonwilds.Enabled)
                    _gameIntegration = new RunescapeDragonwildsIntegration();
                else if ((igdbId == WAR_THUNDER_IGDB_ID || gameName?.Equals("War Thunder", StringComparison.OrdinalIgnoreCase) == true) && integrations.WarThunder.Enabled)
                    _gameIntegration = new WarThunderIntegration();
                else if ((igdbId == OVERWATCH_IGDB_ID || gameName?.Equals("Overwatch", StringComparison.OrdinalIgnoreCase) == true) && integrations.Overwatch.Enabled)
                {
                    // Deliberately empty: Overwatch exposes no log or local API, so there is no
                    // Integration to construct — its events come from the ML detector below, off
                    // the same toggle. Kept so the chain stops here.
                }
#if WINDOWS
                else if ((igdbId == GTA_V_IGDB_ID || igdbId == FIVEM_IGDB_ID || igdbId == RAGE_MP_IGDB_ID
                          || gameName?.Contains("Grand Theft Auto", StringComparison.OrdinalIgnoreCase) == true
                          || gameName?.Contains("FiveM", StringComparison.OrdinalIgnoreCase) == true
                          || gameName?.Contains("Rage Multiplayer", StringComparison.OrdinalIgnoreCase) == true) && integrations.Gta.Enabled)
                    _gameIntegration = new GtaIntegration();
#endif

                if (_gameIntegration != null)
                {
                    _gameIntegration.ExePath = exePath;
                    Log.Information($"Starting game integration for IGDB ID: {igdbId}, Game: {gameName}");
                    _ = _gameIntegration.Start();
                }

                var modelId = ResolveModelId(igdbId, gameName);
                if (modelId != null && ModelService.HasModelForGame(modelId))
                {
                    // Only start ML detection if the integration is enabled
                    bool mlEnabled = IsMlDetectionEnabled(modelId, integrations);

                    if (!mlEnabled)
                    {
                        Log.Information("ML detection skipped for {GameName} ({ModelId}) — integration disabled", gameName, modelId);
                    }
                    else
                    {
                        var session = new DetectionSession(
                            new VisualEventDetector(500),
                            new CooldownTracker(),
                            ModelService.LoadEventDefinitions(modelId));

                        // The handler closes over `session`, never over the static field, so it
                        // cannot observe a half-torn-down state no matter what Start or Shutdown
                        // does on another thread while a detection cycle is in flight.
                        session.Detector.DetectionsAvailable += detections => HandleDetections(session, detections);

                        // Published before Start so a Start that throws part-way still leaves the
                        // detector reachable for the next call to stop.
                        _detectionSession = session;
                        session.Detector.Start(modelId);
                        Log.Information("ML detection started for {GameName} using model {ModelId}", gameName, modelId);
                    }
                }
                else if (modelId != null)
                {
                    // Debug, not Information: most games have no model at all, and this runs on
                    // every recording start. It exists so a name that resolved to an unexpected
                    // id is visible instead of silently doing nothing.
                    Log.Debug("No ML model on disk for {ModelId} (game {GameName})", modelId, gameName);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        // Everything a detection cycle needs arrives as arguments rather than being read back off
        // the statics, which is what makes the callback safe to run while Start or Shutdown is
        // swapping the current session. Static so it cannot accidentally regain that access.
        internal static void HandleDetections(DetectionSession session, List<DetectionResult> detections)
        {
            var defs = session.EventDefinitions;
            var now = DateTime.Now;

            bool exclusionActive = detections.Any(d =>
            {
                var def = defs.FirstOrDefault(ev => ev.ClassId == d.ClassId);
                return def != null && def.Type == EventType.Exclusion;
            });

            foreach (var detection in detections)
            {
                var def = defs.FirstOrDefault(d => d.ClassId == detection.ClassId);
                if (def == null || def.Type == EventType.Exclusion) continue;
                if (exclusionActive)
                {
                    Log.Debug("Suppressing trigger {ClassId} ({Name}) due to active exclusion",
                        detection.ClassId, def.Name);
                    continue;
                }
                session.CooldownTracker.ProcessDetection(detection, def, now);
            }

            session.CooldownTracker.Cleanup(now);
        }

        public static async Task Shutdown()
        {
            await _lock.WaitAsync();
            try
            {
                var session = Interlocked.Exchange(ref _detectionSession, null);
                session?.Detector.Stop();

                if (_gameIntegration != null)
                {
                    Log.Information("Shutting down game integration");
                    await _gameIntegration.Shutdown();
                    _gameIntegration = null;
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        // Resolves the data/training subdirectory holding a game's model. A recognized IGDB id wins
        // because it survives name drift; the sanitized display name is only a fallback for games
        // we have no id mapping for.
        internal static string? ResolveModelId(int? igdbId, string? gameName)
        {
            if (igdbId.HasValue && _modelIdsByIgdbId.TryGetValue(igdbId.Value, out var mappedId))
                return mappedId;

            if (gameName == null)
                return null;

            var sanitized = SanitizeGameId(gameName);
            if (sanitized.Length == 0)
                return null;

            // Canonicalise onto the mapped spelling so a name-matched game and an id-matched one
            // share one ModelService cache entry instead of loading the ONNX session twice.
            foreach (var knownId in _modelIdsByIgdbId.Values)
            {
                if (knownId.Equals(sanitized, StringComparison.OrdinalIgnoreCase))
                    return knownId;
            }

            return sanitized;
        }

        // Model ids that have a user-facing integration toggle honour it; every other model runs.
        // Keyed off the resolved model id, not the display name, so the toggle can never disagree
        // with the model lookup ResolveModelId performed.
        internal static bool IsMlDetectionEnabled(string modelId, GameIntegrations integrations)
        {
            if (modelId.Equals(OVERWATCH_MODEL_ID, StringComparison.OrdinalIgnoreCase))
                return integrations.Overwatch.Enabled;

            return true;
        }

        private static string SanitizeGameId(string gameName)
        {
            return string.Concat(gameName.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                .Trim().ToLowerInvariant();
        }
    }
}
