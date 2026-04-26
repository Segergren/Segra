using Segra.Backend.Core.Models;
using Segra.Backend.Games;
using Segra.Backend.Games.CounterStrike2;
using Segra.Backend.Games.LeagueOfLegends;
using Segra.Backend.Games.Pubg;
using Segra.Backend.Games.RocketLeague;
using Serilog;

namespace Segra.Backend.Services
{
    public static class GameIntegrationService
    {
        private const int PUBG_IGDB_ID = 27789;
        private const int LOL_IGDB_ID = 115;
        private const int CS2_IGDB_ID = 242408;
        private const int ROCKET_LEAGUE_IGDB_ID = 11198;
        private static Integration? _gameIntegration;
        public static Integration? GameIntegration => _gameIntegration;

        public static async Task Start(int? igdbId, string? gameName = null)
        {
            if (_gameIntegration != null)
            {
                Log.Information("Active game integration already exists! Shutting down before starting");
                await _gameIntegration.Shutdown();
            }

            if ((igdbId == PUBG_IGDB_ID || gameName?.Contains("PUBG:", StringComparison.OrdinalIgnoreCase) == true || gameName?.Contains("PLAYERUNKNOWN'S BATTLEGROUNDS", StringComparison.OrdinalIgnoreCase) == true) && Settings.Instance.GameIntegrations.Pubg.Enabled)
                _gameIntegration = new PubgIntegration();
            else if ((igdbId == LOL_IGDB_ID || gameName?.Equals("League of Legends", StringComparison.OrdinalIgnoreCase) == true) && Settings.Instance.GameIntegrations.LeagueOfLegends.Enabled)
                _gameIntegration = new LeagueOfLegendsIntegration();
            else if ((igdbId == CS2_IGDB_ID || gameName?.Equals("Counter-Strike 2", StringComparison.OrdinalIgnoreCase) == true) && Settings.Instance.GameIntegrations.CounterStrike2.Enabled)
                _gameIntegration = new CounterStrike2Integration();
            else if ((igdbId == ROCKET_LEAGUE_IGDB_ID || gameName?.Equals("Rocket League", StringComparison.OrdinalIgnoreCase) == true) && Settings.Instance.GameIntegrations.RocketLeague.Enabled)
                _gameIntegration = new RocketLeagueIntegration();

            if (_gameIntegration == null)
                return;

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
