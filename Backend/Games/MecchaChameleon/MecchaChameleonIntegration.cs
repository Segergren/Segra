using Segra.Backend.Core.Models;
using System.Text.RegularExpressions;

namespace Segra.Backend.Games.MecchaChameleon
{
    /// <summary>
    /// MECCHA CHAMELEON is a hide-and-seek shooter. Its kill feed prints a shared line,
    /// "&lt;seeker&gt; found &lt;hider&gt;", visible to every player whenever anyone is caught —
    /// not just the two players involved. To turn that into a Kill (for the seeker) or a
    /// Death (for the hider) *for the local player specifically*, we resolve which side (if
    /// either) matches the local player's in-game name. That name is the custom display name
    /// the player set in-game (read from the Unreal save via <see cref="MecchaSaveReader"/>),
    /// falling back to their Steam persona name when no custom name is set.
    /// </summary>
    internal class MecchaChameleonIntegration : OcrIntegration
    {
        private static readonly Regex FoundPattern = new(@"(.+?)\s*found\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "MECCHA",
            // The kill feed is stylized, colored text that the built-in Windows OCR can't read
            // reliably (it returned digits for "Bib found Pappa"), so this integration uses PaddleOCR.
            UsePaddleOcr = true,
            // Fractional crop around the kill-feed line, tuned against a 2560x1440 reference
            // screenshot: text spans x[1045,1545] y[1125,1205] -> 1045/2560, 1125/1440, 500/2560, 80/1440.
            // Width is padded slightly beyond that (0.195 -> 0.225) to catch longer names, keeping the
            // same horizontal center (0.5055) by shifting X left by half the added width (0.408 -> 0.393).
            CropRegion = new CropRegion(X: 0.393, Y: 0.781, Width: 0.225, Height: 0.056),
            Keywords =
            [
                new()
                {
                    Text = "found",
                    BookmarkType = BookmarkType.Kill, // fallback default; Resolver decides the real outcome
                    PossibleTypes = [BookmarkType.Kill, BookmarkType.Death],
                    Resolver = ResolveFoundEvent,
                },
            ],
            // PaddleOCR is heavier than the built-in Windows OCR, so poll at 750ms instead of the
            // 250ms default — still well inside the ~3s a feed line stays on screen.
            PollIntervalMs = 750,
            EventCooldown = TimeSpan.FromSeconds(3),
        };

        private static BookmarkType? ResolveFoundEvent(string ocrText)
        {
            // Prefer the custom in-game name (what actually shows in the feed); fall back to the
            // Steam persona name when the player hasn't set one.
            var playerName = MecchaSaveReader.GetCustomPlayerName()
                             ?? SteamUtils.GetCurrentUserPersonaName();
            if (string.IsNullOrWhiteSpace(playerName))
                return null;

            var match = FoundPattern.Match(ocrText);
            if (!match.Success)
                return null;

            var seeker = CleanName(match.Groups[1].Value);
            var hider = CleanName(match.Groups[2].Value);
            if (seeker.Length == 0 || hider.Length == 0)
                return null;

            if (NamesMatch(seeker, playerName))
                return BookmarkType.Kill;

            if (NamesMatch(hider, playerName))
                return BookmarkType.Death;

            return null;
        }

        private static string CleanName(string raw) =>
            raw.Trim().Trim('.', ',', '!', ':', ';', '"', '\'');

        /// <summary>
        /// Fuzzy-compares an OCR'd name fragment against the real persona name, tolerating the
        /// misreads OCR commonly produces (dropped/substituted diacritics and letters).
        /// </summary>
        private static bool NamesMatch(string ocrName, string personaName)
        {
            if (ocrName.Equals(personaName, StringComparison.OrdinalIgnoreCase))
                return true;

            // Very short names are too easy to collide with fuzzy matching, require exact match
            if (personaName.Length <= 3)
                return false;

            int maxAllowed = Math.Max(1, personaName.Length / 4);
            return LevenshteinDistance(ocrName.ToLowerInvariant(), personaName.ToLowerInvariant()) <= maxAllowed;
        }
    }
}
