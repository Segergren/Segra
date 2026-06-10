using Segra.Backend.Core.Models;
using Segra.Backend.Games;
using Segra.Backend.Games.CounterStrike2;
using Segra.Backend.Games.Dota2;
using Segra.Backend.Games.GrandTheftAuto;
using Segra.Backend.Games.LeagueOfLegends;
using Segra.Backend.Games.Minecraft;
using Segra.Backend.Games.Pubg;
using Segra.Backend.Games.RocketLeague;
using Segra.Backend.Games.RunescapeDragonwilds;
using Segra.Backend.Games.Rust;
using Segra.Backend.Games.WarThunder;
using Serilog;

namespace Segra.Backend.Services
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
        private const int GTA_V_IGDB_ID = 1020;

        private static Integration? _gameIntegration;

        public static async Task Start(int? igdbId, string? gameName = null, string? exePath = null)
        {
            if (_gameIntegration != null)
            {
                Log.Information("Active game integration already exists! Shutting down before starting");
                await _gameIntegration.Shutdown();
            }

            var integrations = Settings.Instance.GameIntegrations;

            if ((igdbId == PUBG_IGDB_ID || gameName?.Contains("PUBG:", StringComparison.OrdinalIgnoreCase) == true || gameName?.Contains("PLAYERUNKNOWN'S BATTLEGROUNDS", StringComparison.OrdinalIgnoreCase) == true) && integrations.Pubg.Enabled)
                _gameIntegration = new PubgIntegration();
            else if ((igdbId == LOL_IGDB_ID || gameName?.Equals("League of Legends", StringComparison.OrdinalIgnoreCase) == true) && integrations.LeagueOfLegends.Enabled)
                _gameIntegration = new LeagueOfLegendsIntegration();
            else if ((igdbId == CS2_IGDB_ID || gameName?.Equals("Counter-Strike 2", StringComparison.OrdinalIgnoreCase) == true) && integrations.CounterStrike2.Enabled)
                _gameIntegration = new CounterStrike2Integration();
            else if ((igdbId == ROCKET_LEAGUE_IGDB_ID || gameName?.Equals("Rocket League", StringComparison.OrdinalIgnoreCase) == true) && integrations.RocketLeague.Enabled)
                _gameIntegration = new RocketLeagueIntegration();
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
            else if ((igdbId == GTA_V_IGDB_ID || gameName?.Contains("Grand Theft Auto", StringComparison.OrdinalIgnoreCase) == true) && integrations.Gta.Enabled)
                _gameIntegration = new GtaIntegration();

            if (_gameIntegration == null)
                return;

            _gameIntegration.ExePath = exePath;
            Log.Information($"Starting game integration for IGDB ID: {igdbId}, Game: {gameName}");
            _ = _gameIntegration.Start();
        }

        public static async Task Shutdown()
        {
            if (_gameIntegration == null)
            {
                return;
            }

            Log.Information("Shutting down game integration");
            await _gameIntegration.Shutdown();
            _gameIntegration = null;
        }
    }
}
