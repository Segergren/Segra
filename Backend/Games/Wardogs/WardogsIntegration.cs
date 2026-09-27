using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.Wardogs
{
    internal class WardogsIntegration : OcrIntegration
    {
        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "WARDOGS",
            // Wide enough for 16:10, where the centered prompts spread further across the width
            CropRegion = new CropRegion(X: 0.36, Y: 0.68, Width: 0.28, Height: 0.25),
            Keywords =
            [
                // Downed prompt, stays up until revive or respawn
                new() { Text = "CALL FOR HELP", BookmarkType = BookmarkType.Death,
                        Cooldown = TimeSpan.FromSeconds(15), ExtendCooldownWhileVisible = true },
                // "KILL CONFIRMED", but "KILL" is often misread at 720p
                new() { Text = "CONFIRMED", BookmarkType = BookmarkType.Kill,
                        ExcludeFragments = ["ASSIST"] },
            ],
            // Binarizing breaks the small prompt text below 1440p
            Threshold = 0,
        };
    }
}
