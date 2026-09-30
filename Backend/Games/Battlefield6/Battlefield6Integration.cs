using Serilog;
using ObsKit.NET.Sources;
using Segra.Backend.Core.Models;
using System.Text.RegularExpressions;

namespace Segra.Backend.Games.Battlefield6
{
    internal class Battlefield6Integration : OcrIntegration
    {
        // Label row of the kill card below the crosshair, which sits about 18 px lower (at 1080p) in some game versions
        private static readonly CropRegion LabelRegion = new(X: 0.15, Y: 0.692, Width: 0.30, Height: 0.063);
        // Ribbon above the card
        private static readonly CropRegion RibbonRegion = new(X: 0.18, Y: 0.58, Width: 0.24, Height: 0.055);

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
                .Select(word => new string(word.Text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
                .Where(word => word.Length > 0)
                .ToList();

            var now = DateTime.UtcNow;
            int kills = _labels.Update(words, now);
            if (_labels.CardVisible(now))
            {
                var ribbon = await Recognize(source, RibbonRegion, 0).ConfigureAwait(false);
                int streak = ribbon == null ? 0 : FuzzyContains(ribbon.Text, "TRIPLE KILL") ? 3 : FuzzyContains(ribbon.Text, "DOUBLE KILL") ? 2 : 0;
                kills += _labels.UpdateStreak(streak, now);
            }

            for (int i = 0; i < kills; i++)
            {
                AddBookmark(BookmarkType.Kill);
                Log.Information($"[BF6] Detected '{result.Text}' label -> Kill");
            }
        }

        // A kill adds KILL or HEADSHOT to the card, repeats in the same combo read "KILL x2"
        private class LabelCounter
        {
            // A card shows its labels for about 2.5 s after the last kill, the next card can come right after
            private static readonly TimeSpan CardLifetime = TimeSpan.FromSeconds(3);
            // Kills of a DOUBLE or TRIPLE KILL streak are up to this far apart
            private static readonly TimeSpan StreakGap = TimeSpan.FromSeconds(6);
            private readonly Dictionary<string, (int Count, DateTime Time)> _counted = [];
            private readonly List<DateTime> _kills = [];
            private Dictionary<string, int> _visible = [];
            private DateTime _rowSeen, _labelSeen, _streakShown, _streakSeen;
            private int _streak;

            public bool CardVisible(DateTime now) => now - _labelSeen <= TimeSpan.FromSeconds(2);

            // Returns how many kills the row added since the last update
            public int Update(List<string> words, DateTime now)
            {
                if (words.Where(word => word.All(char.IsLetter)).Sum(word => word.Length) >= 4)
                    _rowSeen = now;
                else if (now - _rowSeen >= TimeSpan.FromSeconds(0.5))
                {
                    // The card closed, the next one counts its labels from one again
                    foreach (var label in _counted.Where(entry => now - entry.Value.Time >= CardLifetime).Select(entry => entry.Key).ToList())
                        _counted.Remove(label);
                }

                _visible = CountLabels(words);
                if (_visible.Count > 0)
                    _labelSeen = now;

                int added = 0;
                foreach (var (label, count) in _visible)
                {
                    int counted = _counted.GetValueOrDefault(label).Count;
                    if (count <= counted)
                        continue;

                    _counted[label] = (count, now);
                    added += count - counted;
                }
                AddKills(added, now);
                return added;
            }

            // A new card with the same label can replace the previous one without a gap, the streak ribbon still counts it
            public int UpdateStreak(int streak, DateTime now)
            {
                if (streak == 0)
                    return 0;
                if (streak != _streak || now - _streakSeen > TimeSpan.FromSeconds(1.5))
                    _streakShown = now;
                _streak = streak;
                _streakSeen = now;

                // "KILL x2" can update after the ribbon shows up
                if (now - _streakShown < TimeSpan.FromSeconds(1))
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
                _kills.RemoveAll(time => now - time > TimeSpan.FromSeconds(30));
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
