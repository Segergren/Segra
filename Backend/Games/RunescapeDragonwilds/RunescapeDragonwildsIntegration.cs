using Serilog;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.RunescapeDragonwilds
{
    internal class RunescapeDragonwildsIntegration : LogTailIntegration
    {
        protected override string LogPrefix => "RSDW";

        private const string DeathMarker = "Changing input mode from [DIM_Gameplay] to [DIM_Dead]";

        protected override string? ResolveLogPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string candidate = Path.Combine(localAppData, "RSDragonwilds", "Saved", "Logs", "RSDragonwilds.log");
            if (File.Exists(candidate)) return candidate;
            return null;
        }

        protected override void ProcessLine(string line)
        {
            if (line.Contains(DeathMarker, StringComparison.Ordinal))
            {
                Log.Information($"RSDW death detected: {line.Trim()}");
                AddBookmark(BookmarkType.Death);
            }
        }
    }
}
