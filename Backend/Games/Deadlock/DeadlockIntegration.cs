using Serilog;
using ObsKit.NET.Sources;
using Segra.Backend.Core.Models;
using global::Windows.Media.Ocr;
using System.Text.RegularExpressions;

namespace Segra.Backend.Games.Deadlock
{
    internal class DeadlockIntegration : OcrIntegration
    {
        // Hero badges above the crosshair, a killed or assisted enemy's badge shows KILL or ASSIST above its soul share
        private static readonly CropRegion BadgeRegion = new(X: 0.35, Y: 0.235, Width: 0.30, Height: 0.075);
        // The badge text is small, so OCR reads the screen at twice the size of 1440p
        private const int ReferenceHeight = 2880;

        private readonly BadgeTracker _badges = new(ReferenceHeight, BadgeRegion);
        private DateTime _deadUntil;

        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "Deadlock",
            // "Respawn in: N" box, handled in ProcessText
            CropRegion = new CropRegion(X: 0.40, Y: 0.72, Width: 0.20, Height: 0.08),
            Keywords = [],
            Threshold = 0,
            ReferenceHeight = ReferenceHeight,
            PollIntervalMs = 500,
        };

        protected override void ProcessText(string text)
        {
            if (!FuzzyContains(text, "Respawn in"))
                return;

            var now = DateTime.UtcNow;
            if (now >= _deadUntil)
            {
                AddBookmark(BookmarkType.Death);
                Log.Information("[Deadlock] Detected 'Respawn in' in OCR text -> Death");
            }

            // Opening the shop hides the box, so the countdown decides when the next death can start.
            // Lone digits rarely OCR, so an unread number means up to 10 s are left.
            var countdown = Regex.Match(text, @"in\W*([0-9]{1,2})\b", RegexOptions.IgnoreCase);
            var seconds = countdown.Success ? int.Parse(countdown.Groups[1].Value) : 0;
            var until = now.AddSeconds(Math.Max(seconds, 10));
            if (until > _deadUntil)
                _deadUntil = until;
        }

        protected override async Task OnPoll(GameCapture source)
        {
            var result = await Recognize(source, BadgeRegion, 0).ConfigureAwait(false);
            if (result == null)
                return;

            var now = DateTime.UtcNow;
            foreach (var badge in _badges.Update(result, (double)source.Width / source.Height, now))
            {
                AddBookmark(badge.Type, DateTime.Now - (now - badge.FirstSeen));
                Log.Information($"[Deadlock] Detected {badge.Type} badge ({(badge.Amount.Length > 0 ? badge.Amount + " souls" : "no souls")}{(badge.Labeled ? "" : ", label unread")})");
            }
        }

        private static string Clean(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        private static bool IsAmount(string word)
        {
            int digits = word.Count(char.IsDigit);
            return digits >= 2 && digits * 2 >= word.Length;
        }

        // KILL is drawn in the enemy color over the red badge and garbles into "KU-L", "XU-L", "Kit-L", "KILC" or "KILEY", not "KILLS" from the shop
        private static bool IsKill(string word) =>
            word.Length is >= 3 and <= 5 &&
            ((word[0] is 'K' or 'X' && word.Skip(1).All("ILUTNJEC".Contains)) || (word.StartsWith("KIL") && word[^1] != 'S'));

        // Not "RESIST" from the shop
        private static bool IsAssist(string word) => word.StartsWith("AS") && LevenshteinDistance(word, "ASSIST") <= 2;

        private readonly record struct DetectedBadge(BookmarkType Type, DateTime FirstSeen, string Amount, bool Labeled);

        // Badges stay up for about 4.5 s, longer while the fight goes on, several show side by side and the row shifts
        // when one comes or goes. Labels often go unread, so a badge is followed by its soul share and counts once a label was read.
        private class BadgeTracker(double screenHeight, CropRegion region)
        {
            private static readonly TimeSpan Gone = TimeSpan.FromSeconds(3);
            // A share read this often where a badge can be, with no label above it, is a kill. ASSIST is bright and rarely missed.
            private const int UnlabeledReads = 4;
            // The row is centered on the screen and its badges are this far apart, in screen heights
            private const double HalfSlot = 0.0458;

            private readonly List<Badge> _badges = [];

            private class Badge
            {
                public required DateTime First;
                public required double X;
                // Empty until the soul share is read, Street Brawl shows none
                public string Amount = "";
                public DateTime Last;
                public int AmountReads, KillReads, AssistReads;
                // Reads with a name above the number, an objective or camp over its health or a stat in the shop
                public int NameReads;
                // Read among other numbers, a scoreboard
                public bool InTable;
                // Reads where the row puts a share
                public int SlotReads;
                public bool Counted;
            }

            // CanStart: uppercase and alone on its line like a badge label, unlike item descriptions and banners
            private readonly record struct Word(string Text, double X, double Y, bool CanStart);

            public List<DetectedBadge> Update(OcrResult result, double aspectRatio, DateTime now)
            {
                _badges.RemoveAll(b => now - b.Last > Gone);

                var words = result.Lines.SelectMany(line =>
                    {
                        bool prose = line.Words.Select(w => Clean(w.Text)).Any(c => c.Length >= 4 && !IsAmount(c) && !IsKill(c) && !IsAssist(c));
                        return line.Words.Select(w => new Word(
                            Clean(w.Text),
                            w.BoundingRect.X + w.BoundingRect.Width / 2,
                            w.BoundingRect.Y + w.BoundingRect.Height / 2,
                            !prose && w.Text.Count(char.IsUpper) * 2 >= w.Text.Count(char.IsLetter)));
                    })
                    .Where(w => w.Text.Length > 0)
                    .ToList();
                // Labels sit at 0.27 of the screen height, 0.28 in Street Brawl, the share under them at 0.29
                var amounts = words.Where(w => IsAmount(w.Text) && InRows(w, 0.275, 0.31)).OrderBy(w => w.X).ToList();
                var labels = words.Where(w => !IsAmount(w.Text) && (IsKill(w.Text) || IsAssist(w.Text)) && InRows(w, 0.25, 0.29)).ToList();
                // Scoreboards and the shop show tables of numbers
                bool table = words.Count(w => IsAmount(w.Text)) >= 3;
                var seen = new HashSet<Badge>();
                var voted = new HashSet<Badge>();
                var usedLabels = new HashSet<Word>();

                // Same share first, anywhere in the row as it shifts, then a near read of it close by, then a badge only known by its label
                var matched = new Badge?[amounts.Count];
                for (int pass = 0; pass < 3; pass++)
                {
                    for (int i = 0; i < amounts.Count; i++)
                    {
                        if (matched[i] != null)
                            continue;
                        var amount = amounts[i];
                        var badge = _badges
                            .Where(b => !seen.Contains(b) && pass switch
                            {
                                0 => SameAmount(b.Amount, amount.Text) && (Digits(amount.Text).Length >= 3 || Math.Abs(b.X - amount.X) < 0.12 * screenHeight),
                                1 => NearAmount(b.Amount, amount.Text) && Math.Abs(b.X - amount.X) < 0.06 * screenHeight,
                                _ => b.Amount.Length == 0 && Math.Abs(b.X - amount.X) < 0.06 * screenHeight,
                            })
                            .MinBy(b => Math.Abs(b.X - amount.X));
                        if (badge != null)
                        {
                            matched[i] = badge;
                            seen.Add(badge);
                        }
                    }
                }

                for (int i = 0; i < amounts.Count; i++)
                {
                    var amount = amounts[i];
                    var badge = matched[i];
                    if (badge == null)
                    {
                        badge = new Badge { First = now, X = amount.X };
                        _badges.Add(badge);
                        seen.Add(badge);
                    }
                    badge.X = amount.X;
                    badge.Amount = amount.Text;
                    badge.Last = now;
                    badge.AmountReads++;
                    badge.InTable |= table;
                    if (InSlot(amount, aspectRatio))
                        badge.SlotReads++;

                    var above = words.Where(w => !IsAmount(w.Text) && IsAbove(w, amount)).ToList();
                    var label = above.FirstOrDefault(labels.Contains);
                    if (label == default)
                    {
                        if (above.Any(w => w.Text.Length >= 4))
                            badge.NameReads++;
                        continue;
                    }
                    Vote(badge, label);
                    voted.Add(badge);
                    usedLabels.Add(label);

                    // The same badge seen earlier by its label alone, before the row shifted
                    foreach (var early in _badges.Where(b => !seen.Contains(b) && b.Amount.Length == 0 && Math.Abs(b.X - badge.X) < 0.06 * screenHeight).ToList())
                    {
                        badge.KillReads += early.KillReads;
                        badge.AssistReads += early.AssistReads;
                        badge.Counted |= early.Counted;
                        if (early.First < badge.First)
                            badge.First = early.First;
                        _badges.Remove(early);
                    }
                }

                // A label read without its share goes to the closest badge with the same label, wherever the row moved it,
                // or to a badge right below it. More labels than badges start new ones.
                foreach (var label in labels.Where(l => !usedLabels.Contains(l)).OrderBy(l => l.X))
                {
                    bool assist = IsAssist(label.Text);
                    var badge = _badges
                        .Where(b => !voted.Contains(b) && (b.KillReads + b.AssistReads > 0
                            ? b.AssistReads > 0 == assist
                            : Math.Abs(b.X - label.X) < 0.03 * screenHeight))
                        .MinBy(b => Math.Abs(b.X - label.X));
                    if (badge == null)
                    {
                        if (!label.CanStart)
                            continue;
                        badge = new Badge { First = now, X = label.X };
                        _badges.Add(badge);
                    }
                    Vote(badge, label);
                    voted.Add(badge);
                    if (seen.Add(badge))
                        badge.Last = now;
                }

                var detected = new List<DetectedBadge>();
                foreach (var badge in _badges.Where(b => !b.Counted))
                {
                    // A share misread once must not become a badge of its own
                    int votes = badge.KillReads + badge.AssistReads;
                    bool labeled = votes > 0 && votes >= badge.NameReads && (badge.Amount.Length > 0 ? badge.AmountReads >= 2 : votes >= 2);
                    var digits = Digits(badge.Amount);
                    bool unlabeled = badge.SlotReads >= UnlabeledReads && badge.NameReads == 0 && !badge.InTable && digits.Length >= 3 && digits.Trim('0').Length > 0;
                    if (!labeled && !unlabeled)
                        continue;
                    badge.Counted = true;
                    detected.Add(new DetectedBadge(badge.AssistReads > 0 ? BookmarkType.Assist : BookmarkType.Kill, badge.First, badge.Amount, labeled));
                }
                return detected;
            }

            private static void Vote(Badge badge, Word label)
            {
                if (IsAssist(label.Text))
                    badge.AssistReads++;
                else
                    badge.KillReads++;
            }

            private bool InRows(Word word, double top, double bottom)
            {
                double y = region.Y + word.Y / screenHeight;
                return y >= top && y <= bottom;
            }

            // A share where the row puts one, a shop price or other number elsewhere rarely lines up
            private bool InSlot(Word amount, double aspectRatio)
            {
                double offset = amount.X / screenHeight - (0.5 - region.X) * aspectRatio;
                double slot = offset - Math.Round(offset / HalfSlot) * HalfSlot;
                return Math.Abs(slot) < 0.007 && InRows(amount, 0.283, 0.3);
            }

            // Label centered over the share, just above it
            private bool IsAbove(Word label, Word amount)
            {
                double rise = amount.Y - label.Y;
                return Math.Abs(label.X - amount.X) < 0.03 * screenHeight && rise > 0.008 * screenHeight && rise < 0.04 * screenHeight;
            }

            // The soul icon in front of the share reads as a digit, "4387" for 387
            private static bool SameAmount(string a, string b)
            {
                string x = Digits(a), y = Digits(b);
                return x.Length >= 2 && y.Length >= 2 && (x.EndsWith(y) || y.EndsWith(x));
            }

            private static bool NearAmount(string a, string b)
            {
                string x = Digits(a), y = Digits(b);
                return x.Length > 0 && y.Length > 0 && LevenshteinDistance(x, y) <= 1;
            }

            private static string Digits(string word) =>
                new(word.Replace('S', '5').Replace('O', '0').Replace('I', '1').Replace('B', '8').Where(char.IsDigit).ToArray());
        }
    }
}
