using Segra.Backend.GameIntegration;
using Serilog;

namespace Segra.Backend.Services
{
    public static class GameIntegrationService
    {
        private const string PUBG = "PUBG: BATTLEGROUNDS";
        private const string LOL = "League of Legends";
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
