using Segra.Backend.Games;
using Segra.Backend.Games.CounterStrike2;
using Segra.Backend.Games.LeagueOfLegends;
using Segra.Backend.Games.Pubg;
using Serilog;

namespace Segra.Backend.Services
{
    public static class GameIntegrationService
    {
        private const string PUBG = "PUBG: BATTLEGROUNDS";
        private const string LOL = "League of Legends";
        private const string CS2 = "Counter-Strike 2";
        private static Integration? _gameIntegration;
        public static Integration? GameIntegration => _gameIntegration;

        public static async Task Start(string gameName)
        {
            if (_gameIntegration != null)
            {
                Log.Information("Active game integration already exists! Shutting down before starting");
                await _gameIntegration.Shutdown();
            }

            _gameIntegration = gameName switch
            {
                PUBG => new PubgIntegration(),
                LOL => new LeagueOfLegendsIntegration(),
                CS2 => new CounterStrike2Integration(),
                _ => null,
            };

            if (_gameIntegration == null)
            {
                return;
            }

            Log.Information($"Starting game integration for: {gameName}");
            await _gameIntegration.Start();
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
