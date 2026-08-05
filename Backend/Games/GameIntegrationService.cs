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

        private static Integration? _gameIntegration;
        private static readonly SemaphoreSlim _lock = new(1, 1);
        private static VisualEventDetector? _visualDetector;
        private static CooldownTracker? _cooldownTracker;
        private static List<EventDefinition>? _eventDefinitions;

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
                    _gameIntegration = null;
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

                if (gameName != null)
                {
                    var safeGameId = SanitizeGameId(gameName);
                    if (ModelService.HasModelForGame(safeGameId))
                    {
                        // Only start ML detection if the integration is enabled
                        bool mlEnabled = (igdbId == OVERWATCH_IGDB_ID || gameName.Equals("Overwatch", StringComparison.OrdinalIgnoreCase))
                            ? integrations.Overwatch.Enabled
                            : true;

                        if (!mlEnabled)
                        {
                            Log.Information("ML detection skipped for {GameName} — integration disabled", gameName);
                        }
                        else
                        {
                            _visualDetector?.Stop();
                            _visualDetector = null;
                            _cooldownTracker = null;
                            _eventDefinitions = null;

                            _eventDefinitions = ModelService.LoadEventDefinitions(safeGameId);
                            _cooldownTracker = new CooldownTracker();
                            _visualDetector = new VisualEventDetector(500);
                            _visualDetector.DetectionsAvailable += detections =>
                            {
                                var defs = _eventDefinitions;
                                if (defs == null) return;

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
                                    _cooldownTracker.ProcessDetection(detection, def, now);
                                }
                                _cooldownTracker.Cleanup(now);
                            };
                            _visualDetector.Start(safeGameId);
                            Log.Information("ML detection started for {GameName}", gameName);
                        }
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public static async Task Shutdown()
        {
            await _lock.WaitAsync();
            try
            {
                _visualDetector?.Stop();
                _visualDetector = null;
                _cooldownTracker = null;
                _eventDefinitions = null;

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

        private static string SanitizeGameId(string gameName)
        {
            return string.Concat(gameName.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                .Trim().ToLowerInvariant();
        }
    }
}
