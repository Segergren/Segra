using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.RocketLeague
{
    internal class RocketLeagueIntegration : OcrIntegration
    {
        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "RL",
            CropRegion = new CropRegion(X: 0.38, Y: 0.15, Width: 0.24, Height: 0.10),
            Keywords =
            [
                new() { Text = "+100", BookmarkType = BookmarkType.Goal },
                new() { Text = "goal", BookmarkType = BookmarkType.Goal,
                        ExcludeFragments = ["shot", "on", "sh", " n"] },
                new() { Text = "assist", BookmarkType = BookmarkType.Assist },
            ],
        };
    }
}
