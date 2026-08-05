using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.ApexLegends
{
    internal class ApexLegendsIntegration : OcrIntegration
    {
        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "Apex",
            CropRegion = new CropRegion(X: 0.00, Y: 0.10, Width: 1.00, Height: 0.75),
            Threshold = 125,
            PollIntervalMs = 300,
            EventCooldown = TimeSpan.FromSeconds(8),
            TimeCompensation = TimeSpan.FromSeconds(1.25),
            Keywords =
            [
                new() { Text = "KNOCKED DOWN", BookmarkType = BookmarkType.Kill },
                new() { Text = "SQUAD WIPE", BookmarkType = BookmarkType.Kill },
                new() { Text = "ELIMINATED", BookmarkType = BookmarkType.Kill },
                new() { Text = "ASSIST ELIMINATION", BookmarkType = BookmarkType.Assist },
                new() { Text = "YOU ARE THE CHAMPION", BookmarkType = BookmarkType.Kill, CooldownGroup = "Victory" },
            ],
        };
    }
}
