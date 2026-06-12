using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.GrandTheftAuto
{
    internal class GtaIntegration : OcrIntegration
    {
        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "GTA",
            CropRegion = new CropRegion(X: 0.35, Y: 0.40, Width: 0.30, Height: 0.20),
            Keywords =
            [
                new() { Text = "WASTED", BookmarkType = BookmarkType.Death },
            ],
            Threshold = 180,
            PollIntervalMs = 200,
            EventCooldown = TimeSpan.FromSeconds(3),
        };
    }
}
