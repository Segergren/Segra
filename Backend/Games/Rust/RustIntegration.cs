using Serilog;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.Rust
{
    internal class RustIntegration : LogTailIntegration
    {
        protected override string LogPrefix => "Rust";

        private const string DeathMarker = "You died";

        protected override string? ResolveLogPath()
        {
            if (string.IsNullOrEmpty(ExePath)) return null;
            string? dir = Path.GetDirectoryName(ExePath);
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, "output_log.txt");
        }

        protected override void ProcessLine(string line)
        {
            if (line.Contains(DeathMarker, StringComparison.OrdinalIgnoreCase))
            {
                Log.Information($"Rust death detected: {line.Trim()}");
                AddBookmark(BookmarkType.Death);
            }
        }
    }
}
