using Serilog;
using ObsKit.NET.Sources;
using Segra.Backend.Core.Models;
using System.Text.RegularExpressions;

namespace Segra.Backend.Games.Battlefield6
{
    /// <summary>
    /// Detects kills and deaths from the HUD, Battlefield 6 has no log or API for them.
    ///
    /// Kills come from the kill card that shows below-left of the crosshair:
    /// <list type="bullet">
    /// <item>Label row: every kill adds a KILL or HEADSHOT label, and kills in the same combo turn it into "KILL x2".
    /// Each label's highest count on the card is a kill count, which resets when the card closes.</item>
    /// <item>Skull: the kill skull next to the combo points, found by template matching. It shows before the labels
    /// and stays readable on bright ground where they fade, so it counts the card's first kill, which the first label
    /// read then takes over.</item>
    /// <item>Ribbon: DOUBLE, TRIPLE and QUAD KILL, then MARAUDER 5, 6... for each further kill. When it shows more
    /// kills in a row than were counted, the missing ones are added. This catches cards that replace each other
    /// without a gap and multipliers that OCR drops.</item>
    /// </list>
    ///
    /// Deaths come from the killer card in the bottom right (PLAYER CARD, DAMAGE LOG, the "YOU n FOE" header), which
    /// every death shows, fatal ones included. A down that gets revived is also bookmarked as a death.
    ///
    /// Crops are relative to the screen and scaled to a 2160 px reference height, so any 16:9 resolution works.
    /// Only the English UI is supported.
    /// </summary>
    internal class Battlefield6Integration : OcrIntegration
    {
        // Label row of the kill card below the crosshair, which sits about 18 px lower (at 1080p) in some game versions
        private static readonly CropRegion LabelRegion = new(X: 0.15, Y: 0.692, Width: 0.30, Height: 0.063);
        // Ribbon above the card
        private static readonly CropRegion RibbonRegion = new(X: 0.18, Y: 0.58, Width: 0.24, Height: 0.055);
        // Skull left of the combo points, it stays bright on sunlit ground where the labels fade
        private static readonly CropRegion SkullRegion = new(X: 0.30, Y: 0.624, Width: 0.12, Height: 0.076);
        // The kill skull at 540 px screen height, brightest of R, G and B so white and red (headshot) skulls match alike.
        // The assist skull has a bar under it and stays below the match threshold.
        private const int SkullWidth = 14, SkullHeight = 17;
        private static readonly (float[] Values, double Norm) Skull = LoadSkull(
            "FBQRGSIhIiQlIhMWGh4XGDKQur2/wL2xaRoXGxxBqMHExsfHxsO7bhsZLJTExMbIyMjIxMK6TBhRu8bIyMjIyMfEwsCWHHfCxsfIyMjIx8TBvrI0dMLFxsjIyMjHxMC9qi5eusLFyMnKy8rGwLqeGVCXvrnDyczOyMK/rYETQIKUSWGwzMiOWmGZTRBCpYkrJJbLv0wZP7BeEma8uIyGvqu4nX2hvp8aSq7Hxsa+WpHFyMO9dRg0WIKgycm6wci3fm0tGDIwK1G8xsXGxowUFBgYMTAuM3Gaq6aFQhMWFxgyMDAuLzU4NSceFhQWGA==");

        private readonly LabelCounter _labels = new();

        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "BF6",
            // Killer card, which every death shows until revive or respawn.
            // The revive prompt moves with the nearby medics list and fatal deaths have none.
            CropRegion = new CropRegion(X: 0.79, Y: 0.61, Width: 0.20, Height: 0.365),
            Keywords =
            [
                // Short cooldown, players get downed again seconds after a revive
                new() { Text = "PLAYER CARD", BookmarkType = BookmarkType.Death,
                        Cooldown = TimeSpan.FromSeconds(5), ExtendCooldownWhileVisible = true },
                new() { Text = "DAMAGE LOG", BookmarkType = BookmarkType.Death,
                        Cooldown = TimeSpan.FromSeconds(5), ExtendCooldownWhileVisible = true },
                // "YOU 1 1 FOE" header, the buttons are greyed out on fatal deaths in older versions
                new() { Text = "FOE", BookmarkType = BookmarkType.Death, MatchCase = true,
                        Cooldown = TimeSpan.FromSeconds(5), ExtendCooldownWhileVisible = true },
            ],
            Threshold = 0,
            ReferenceHeight = 2160,
            TimeCompensation = TimeSpan.FromSeconds(0.5),
        };

        protected override async Task OnPoll(GameCapture source)
        {
            var result = await Recognize(source, LabelRegion, 0, localContrast: true).ConfigureAwait(false);
            if (result == null)
                return;

            var words = result.Lines
                .SelectMany(line => line.Words)
                .Select(word => new string(word.Text.Where(char.IsLetterOrDigit).ToArray()))
                // Labels are upper case, challenge popups ("Get headshot hits beyond 75m") are not
                .Where(word => word.Length > 0 && (word.Count(char.IsLetter) < 3 || word.Count(char.IsUpper) * 2 >= word.Count(char.IsLetter)))
                .Select(word => word.ToUpperInvariant())
                .ToList();

            var screenshot = source.TakeScreenshot((uint)(source.Width * SkullRegion.X), (uint)(source.Height * SkullRegion.Y),
                (uint)(source.Width * SkullRegion.Width), (uint)(source.Height * SkullRegion.Height));
            bool skull = screenshot != null && FindSkull(screenshot.Pixels, (int)screenshot.Width, (int)screenshot.Height, source.Height);

            var now = DateTime.UtcNow;
            int kills = _labels.Update(words, skull, now);
            int streak = 0;
            if (_labels.CardVisible(now))
            {
                var ribbon = await Recognize(source, RibbonRegion, 0).ConfigureAwait(false);
                streak = ribbon == null ? 0 : StreakOf(ribbon.Text);
            }
            kills += _labels.UpdateStreak(streak, now);

            for (int i = 0; i < kills; i++)
            {
                AddBookmark(BookmarkType.Kill);
                Log.Information($"[BF6] Detected '{result.Text}' label{(skull ? " and skull" : "")} -> Kill");
            }
        }

        // Multi-kill ribbons: DOUBLE, TRIPLE and QUAD KILL, then MARAUDER 5, 6, 7... for each further kill
        private static int StreakOf(string text)
        {
            if (FuzzyContains(text, "DOUBLE KILL"))
                return 2;
            if (FuzzyContains(text, "TRIPLE KILL"))
                return 3;
            if (FuzzyContains(text, "QUAD KILL"))
                return 4;

            var words = Regex.Replace(text, "[^A-Za-z0-9 ]", " ").ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < words.Length; i++)
            {
                if (words[i].Length >= 6 && LevenshteinDistance(words[i], "MARAUDER") <= 2
                    && Regex.IsMatch(words[i + 1], @"^\d{1,2}$") && int.Parse(words[i + 1]) is >= 5 and var count)
                    return count;
            }
            return 0;
        }

        private static (float[], double) LoadSkull(string base64)
        {
            var bytes = Convert.FromBase64String(base64);
            float mean = (float)bytes.Average(value => (double)value);
            var values = bytes.Select(value => value - mean).ToArray();
            return (values, Math.Sqrt(values.Sum(value => (double)value * value)));
        }

        // Normalized cross-correlation of the skull over the region, scaled to the template's 540 px screen height
        private static bool FindSkull(byte[] pixels, int width, int height, uint sourceHeight)
        {
            double scale = sourceHeight / 540.0;
            int w = (int)(width / scale), h = (int)(height / scale);
            if (w < SkullWidth || h < SkullHeight)
                return false;

            var image = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = (int)(y * scale), y1 = Math.Max((int)((y + 1) * scale), y0 + 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = (int)(x * scale), x1 = Math.Max((int)((x + 1) * scale), x0 + 1);
                    float sum = 0;
                    for (int sy = y0; sy < y1; sy++)
                    {
                        for (int sx = x0; sx < x1; sx++)
                        {
                            int i = (sy * width + sx) * 4;
                            sum += Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                        }
                    }
                    image[y * w + x] = sum / ((x1 - x0) * (y1 - y0));
                }
            }

            int n = SkullWidth * SkullHeight;
            double best = 0;
            int bestX = 0, bestY = 0;
            for (int y = 0; y + SkullHeight <= h; y++)
            {
                for (int x = 0; x + SkullWidth <= w; x++)
                {
                    double dot = 0, sum = 0, sumSquares = 0;
                    for (int j = 0; j < SkullHeight; j++)
                    {
                        for (int i = 0; i < SkullWidth; i++)
                        {
                            float value = image[(y + j) * w + x + i];
                            dot += value * Skull.Values[j * SkullWidth + i];
                            sum += value;
                            sumSquares += value * value;
                        }
                    }
                    double variance = sumSquares - sum * sum / n;
                    if (variance < 1)
                        continue;
                    double score = dot / (Math.Sqrt(variance) * Skull.Norm);
                    if (score > best)
                        (best, bestX, bestY) = (score, x, y);
                }
            }
            if (best < 0.8)
                return false;

            // Dark scenes give noise matches, the real skull is near white (or saturated red)
            var patch = new float[n];
            for (int j = 0; j < SkullHeight; j++)
                Array.Copy(image, (bestY + j) * w + bestX, patch, j * SkullWidth, SkullWidth);
            Array.Sort(patch);
            return patch[n * 9 / 10] >= 180;
        }

        // A kill adds KILL or HEADSHOT to the card, repeats in the same combo read "KILL x2"
        private class LabelCounter
        {
            // A card shows its labels for about 2.5 s after the last kill, the next card can come right after
            private static readonly TimeSpan CardLifetime = TimeSpan.FromSeconds(3);
            // Kills of a multi-kill streak are up to this far apart
            private static readonly TimeSpan StreakGap = TimeSpan.FromSeconds(6);
            private readonly Dictionary<string, (int Count, DateTime Time)> _counted = [];
            private readonly List<DateTime> _kills = [];
            private Dictionary<string, int> _visible = [];
            // Reads of each ribbon value since the first one
            private readonly Dictionary<int, int> _streakReads = [];
            private DateTime _rowSeen, _labelSeen, _streakSince;

            public bool CardVisible(DateTime now) => now - _labelSeen <= TimeSpan.FromSeconds(2);

            // Returns how many kills the row and the skull added since the last update
            public int Update(List<string> words, bool skull, DateTime now)
            {
                if (skull || words.Where(word => word.All(char.IsLetter)).Sum(word => word.Length) >= 4)
                    _rowSeen = now;
                else if (now - _rowSeen >= TimeSpan.FromSeconds(0.5))
                {
                    // The card closed, the next one counts its labels from one again
                    foreach (var label in _counted.Where(entry => now - entry.Value.Time >= CardLifetime).Select(entry => entry.Key).ToList())
                        _counted.Remove(label);
                }

                _visible = CountLabels(words);
                if (_visible.Count > 0 || skull)
                    _labelSeen = now;

                int added = 0;
                foreach (var (label, count) in _visible)
                {
                    int counted = _counted.GetValueOrDefault(label).Count;
                    if (count <= counted)
                        continue;

                    _counted[label] = (count, now);
                    added += count - counted;
                    // The kill counted from the skull is this label's first one
                    if (counted == 0 && _counted.Remove("SKULL"))
                        added--;
                }

                // The skull shows before the labels and stays readable when they are not, it means at least one kill
                if (skull && _counted.Count == 0)
                {
                    _counted["SKULL"] = (1, now);
                    added++;
                }
                AddKills(added, now);
                return added;
            }

            // A new card with the same label can replace the previous one without a gap, the streak ribbon still counts it
            public int UpdateStreak(int streak, DateTime now)
            {
                if (streak > 0)
                {
                    if (_streakReads.Count == 0)
                        _streakSince = now;
                    _streakReads[streak] = _streakReads.GetValueOrDefault(streak) + 1;
                }

                // "KILL x2" can update after the ribbon shows up, so decide a second later.
                // The ribbon itself can be gone by then, the game rotates it with other ribbons.
                if (_streakReads.Count == 0 || now - _streakSince < TimeSpan.FromSeconds(1))
                    return 0;
                // Two reads of the same value, so one misread ribbon or digit adds nothing
                streak = _streakReads.Where(entry => entry.Value >= 2).Select(entry => entry.Key).DefaultIfEmpty().Max();
                _streakReads.Clear();
                if (streak == 0)
                    return 0;

                int counted = 0;
                var last = now;
                for (int i = _kills.Count - 1; i >= 0 && last - _kills[i] <= StreakGap; i--)
                {
                    counted++;
                    last = _kills[i];
                }
                if (counted >= streak)
                    return 0;

                // The labels on screen belong to the kills added here
                foreach (var (label, count) in _visible)
                    _counted[label] = (Math.Max(count, _counted.GetValueOrDefault(label).Count), now);
                AddKills(streak - counted, now);
                return streak - counted;
            }

            private void AddKills(int count, DateTime now)
            {
                // Long enough for a MARAUDER streak, a forgotten kill would make the streak add kills again
                _kills.RemoveAll(time => now - time > TimeSpan.FromMinutes(2));
                for (int i = 0; i < count; i++)
                    _kills.Add(now);
            }

            // The multiplier is usually its own word, sometimes glued on ("KILLX2"), and a box outline can add a digit ("X21")
            private static Dictionary<string, int> CountLabels(List<string> words)
            {
                var counts = new Dictionary<string, int>();
                for (int i = 0; i < words.Count; i++)
                {
                    var glued = Regex.Match(words[i], @"^(\w+?)X([1-9])");
                    var word = glued.Success ? glued.Groups[1].Value : words[i];
                    var label = IsKill(word) ? "KILL" : IsHeadshot(word) ? "HEADSHOT" : null;
                    if (label == null)
                        continue;

                    int count = 1;
                    if (glued.Success)
                        count = int.Parse(glued.Groups[2].Value);
                    else if (i + 1 < words.Count && Regex.Match(words[i + 1], @"^X([1-9])") is { Success: true } next)
                        count = int.Parse(next.Groups[1].Value);
                    counts[label] = Math.Max(counts.GetValueOrDefault(label), count);
                }
                return counts;
            }

            // Shorter reads like "ILL" also come from grass and other textures
            private static bool IsKill(string word) => word.Length >= 4 && LevenshteinDistance(word, "KILL") <= 1;
            private static bool IsHeadshot(string word) => word.Length >= 5 && LevenshteinDistance(word, "HEADSHOT") <= 2;
        }
    }
}
