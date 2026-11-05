using Serilog;
using ObsKit.NET.Sources;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.ModernWarfare4
{
    /// <summary>
    /// Detects kills and deaths on screen, Modern Warfare 4 has no log or API for them.
    ///
    /// The kill feed on the left shows the player's own name in gold, so no name is needed:
    /// <list type="bullet">
    /// <item>Kill: gold text at the start of the row (the killer's column) and an enemy's red name after it.</item>
    /// <item>Death: gold text after an enemy's red name in the killer's column, or the "KILLED BY" header of the killer's
    /// card at the bottom of the screen. Some players close the card at once, and the feed entry can be hidden by bright
    /// scenery, so either one counts.</item>
    /// </list>
    ///
    /// Entries stay up about 5 s, new ones slide in at the bottom and push older ones up. An entry is followed by its row
    /// and the edge of the other player's name, which depends on the names in it.
    ///
    /// Positions are measured from the top left corner and scaled by the screen height.
    /// Only the English UI is supported for the card.
    /// </summary>
    internal class ModernWarfare4Integration : OcrIntegration
    {
        // Kill feed read area at 1080p
        internal const int FeedLeft = 0, FeedRight = 560, FeedTop = 500, FeedBottom = 695;

        private readonly Detector _detector = new();
        private bool _cardSeen;

        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "Modern Warfare 4",
            // "KILLED BY" above the killer's name and card at the bottom of the screen
            CropRegion = new CropRegion(X: 0.30, Y: 0.77, Width: 0.25, Height: 0.08),
            Keywords = [],
            Threshold = 150,
            ReferenceHeight = 1440,
            // The feed entry shows on the frame of the kill, detection is usually within a poll
            TimeCompensation = TimeSpan.FromSeconds(0.3),
        };

        protected override void ProcessText(string text)
        {
            _cardSeen = IsKilledBy(text);
        }

        internal static bool IsKilledBy(string text) => FuzzyContains(text, "KILLED BY");

        // The banner often reads with a letter missing, like "KILL AM"
        internal static bool IsKillcam(string text)
        {
            var letters = new string(text.Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());
            for (int length = 6; length <= 8; length++)
            {
                for (int i = 0; i + length <= letters.Length; i++)
                {
                    if (LevenshteinDistance(letters.Substring(i, length), "KILLCAM") <= 1)
                        return true;
                }
            }
            return false;
        }

        // "KILLCAM" banner at the top of the screen, the killcam shows the card again
        private static readonly CropRegion KillcamRegion = new(X: 0.40, Y: 0.0, Width: 0.20, Height: 0.07);

        protected override async Task OnPoll(GameCapture source)
        {
            bool card = _cardSeen;
            _cardSeen = false;
            // White text over the sky, binarizing loses it
            bool killcam = card && IsKillcam((await Recognize(source, KillcamRegion, 0).ConfigureAwait(false))?.Text ?? "");

            double scale = source.Height / 1080.0;
            uint width = (uint)Math.Min(FeedRight * scale, source.Width);
            var screenshot = source.TakeScreenshot((uint)(FeedLeft * scale), (uint)(FeedTop * scale), width, (uint)((FeedBottom - FeedTop) * scale));
            if (screenshot == null)
                return;

            var (kills, deaths) = _detector.Poll(screenshot.Pixels, (int)screenshot.Width, (int)screenshot.Height, scale, card, killcam, DateTime.Now);
            foreach (var time in kills)
            {
                AddBookmark(BookmarkType.Kill, time);
                Log.Information("[Modern Warfare 4] Detected kill feed entry -> Kill");
            }
            foreach (var time in deaths)
            {
                AddBookmark(BookmarkType.Death, time);
                Log.Information($"[Modern Warfare 4] Detected {(card ? "'KILLED BY'" : "kill feed entry")} -> Death");
            }
        }

        internal class Detector
        {
            // The card shows until the player respawns, OCR misses it in a poll now and then
            private static readonly TimeSpan CardGap = TimeSpan.FromSeconds(1.5);
            // The feed entry of a death can be unreadable over bright scenery for a few seconds after the card,
            // and a player can't respawn and die again sooner than this
            private static readonly TimeSpan MergeGap = TimeSpan.FromSeconds(5);
            // The killcam shows the card again, so soon after a death a card waits a moment for the "KILLCAM" banner
            private static readonly TimeSpan KillcamWindow = TimeSpan.FromSeconds(15);
            private static readonly TimeSpan KillcamWait = TimeSpan.FromSeconds(1);

            private readonly FeedTracker _kills = new(streak: 2);
            // Scenery next to the feed can look like a short-lived entry
            private readonly FeedTracker _deaths = new(streak: 3);
            private DateTime _lastCard = DateTime.MinValue;
            private DateTime _lastDeath = DateTime.MinValue;
            private DateTime? _pendingCard;
            // The player's name is the same width in every entry. Once it is known, gold text of another width
            // (unlock popups, yellow scenery) is dropped.
            private readonly Dictionary<int, int> _nameWidths = [];
            private int _nameWidthSamples;
            private const int NameWidthSamplesNeeded = 20, NameWidthTolerance = 6;

            public (List<DateTime> Kills, List<DateTime> Deaths) Poll(byte[] pixels, int width, int height, double scale, bool card, bool killcam, DateTime now)
            {
                var rows = ReadFeed(pixels, width, height, scale);
                foreach (var row in rows)
                {
                    _nameWidths[row.NameWidth] = _nameWidths.GetValueOrDefault(row.NameWidth) + 1;
                    _nameWidthSamples++;
                }
                if (_nameWidthSamples >= NameWidthSamplesNeeded)
                {
                    int nameWidth = LikelyNameWidth();
                    rows = rows.Where(row => Math.Abs(row.NameWidth - nameWidth) <= NameWidthTolerance).ToList();
                }
                var kills = _kills.Update(rows.Where(row => !row.Death).ToList(), now);
                var feedDeaths = _deaths.Update(rows.Where(row => row.Death).ToList(), now);

                bool newCard = card && now - _lastCard > CardGap;
                var cardTime = now;
                if (card)
                    _lastCard = now;

                if (newCard && now - _lastDeath <= KillcamWindow)
                {
                    _pendingCard = now;
                    newCard = false;
                }
                if (killcam)
                    _pendingCard = null;
                if (_pendingCard is DateTime waited && now - waited >= KillcamWait)
                {
                    (newCard, cardTime) = (true, waited);
                    _pendingCard = null;
                }

                var deaths = new List<DateTime>();
                if ((newCard || feedDeaths.Count > 0) && now - _lastDeath > MergeGap)
                {
                    var time = feedDeaths.Count > 0 ? feedDeaths.Min() : cardTime;
                    if (_pendingCard is DateTime pending && pending < time)
                        time = pending;
                    deaths.Add(time);
                    _lastDeath = now;
                    _pendingCard = null;
                }
                return (kills, deaths);
            }

            // The width with the most rows around it, kill and death rows differ by a few pixels
            private int LikelyNameWidth() => _nameWidths.Keys
                .MaxBy(candidate => _nameWidths.Where(pair => Math.Abs(pair.Key - candidate) <= 3).Sum(pair => pair.Value));
        }

        internal readonly record struct FeedRow(int Y, int Edge, bool Death, int NameWidth);

        // The killer's name starts the row, about 40-65 px from the left edge depending on the HUD bounds setting
        private const int KillerMax = 160;
        // Gold victim names start after a killer's name and weapon icon
        private const int VictimMax = 420;
        // The longest victim names end about here, red scenery further right moves the edge
        private const int VictimEndMax = 450;
        // Text rows, a row of names is about 12 px tall
        private const int MinRowHeight = 6, MaxRowHeight = 18;

        // The player's name, about (220, 183, 90). Orange warnings like packet loss have more red.
        private static bool IsGold(int r, int g, int b) => r >= 170 && g >= 130 && r - b >= 80 && g - b >= 50 && r - g >= 0 && r - g <= 50;

        // Enemy names, about (157, 58, 42)
        private static bool IsRed(int r, int g, int b) => r >= 110 && r - g >= 55 && r - b >= 55;

        // Returns the rows with the player in them at 1080p. Kills are followed by the right edge of the victim's red name,
        // deaths by the left edge of the player's name, which starts after the killer's name.
        internal static List<FeedRow> ReadFeed(byte[] pixels, int width, int height, double scale)
        {
            int goldRight = Math.Min((int)((VictimMax + 150) * scale), width);
            var gold = new bool[height * goldRight];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < goldRight; x++)
                {
                    int i = (y * width + x) * 4;
                    gold[y * goldRight + x] = IsGold(pixels[i + 2], pixels[i + 1], pixels[i]);
                }
            }

            // Consecutive kills get a gold bar left of their rows, taller than any letter
            int barHeight = (int)(MaxRowHeight * scale);
            for (int x = 0; x < goldRight; x++)
            {
                int y = 0;
                while (y < height)
                {
                    if (!gold[y * goldRight + x])
                    {
                        y++;
                        continue;
                    }
                    int start = y;
                    while (y < height && gold[y * goldRight + x])
                        y++;
                    if (y - start > barHeight)
                    {
                        for (int row = start; row < y; row++)
                            gold[row * goldRight + x] = false;
                    }
                }
            }

            var goldPerRow = new int[height];
            var goldLeft = new int[height];
            for (int y = 0; y < height; y++)
            {
                goldLeft[y] = int.MaxValue;
                for (int x = 0; x < goldRight; x++)
                {
                    if (gold[y * goldRight + x])
                    {
                        goldPerRow[y]++;
                        goldLeft[y] = Math.Min(goldLeft[y], x);
                    }
                }
            }

            var rows = new List<FeedRow>();
            int top = -1;
            for (int y = 0; y <= height; y++)
            {
                // Letters leave single empty rows
                if (y < height && (goldPerRow[y] > 0 || (top >= 0 && y + 1 < height && goldPerRow[y + 1] > 0)))
                {
                    if (top < 0)
                        top = y;
                    continue;
                }
                if (top < 0)
                    continue;

                int bandTop = top, bottom = y;
                top = -1;
                double rowHeight = (bottom - bandTop) / scale;
                int count = 0, left = int.MaxValue;
                for (int row = bandTop; row < bottom; row++)
                {
                    count += goldPerRow[row];
                    left = Math.Min(left, goldLeft[row]);
                }
                if (rowHeight < MinRowHeight || rowHeight > MaxRowHeight || count < 25 * scale * scale)
                    continue;

                left = SkipBar(gold, goldRight, bandTop, bottom, left, scale);
                int column = (int)(left / scale) + FeedLeft;
                int nameWidth = (int)((NameEnd(gold, goldRight, bandTop, bottom, left, (int)(10 * scale)) - left) / scale);
                int center = (int)((bandTop + bottom) / 2 / scale) + FeedTop;
                double minRed = 15 * scale * scale;
                // An enemy's red name before the player's name is the killer's, the player died.
                // The edges of a consecutive kills bar are reddish too, but only a few pixels wide.
                var (redBefore, _, redColumns) = CountRed(pixels, width, height, bandTop, bottom, 0, left);
                if (redBefore >= minRed && redColumns >= 8 * scale)
                {
                    if (column <= VictimMax)
                        rows.Add(new FeedRow(center, column, true, nameWidth));
                }
                else if (column <= KillerMax)
                {
                    // The victim's red name after the player's name
                    var (red, edge, _) = CountRed(pixels, width, height, bandTop, bottom, left, (int)(VictimEndMax * scale));
                    if (red >= minRed)
                        rows.Add(new FeedRow(center, (int)(edge / scale) + FeedLeft, false, nameWidth));
                }
            }
            return rows;
        }

        // A consecutive kills bar next to a single row is as tall as the row, it is a thin stroke before the name
        private static int SkipBar(bool[] gold, int goldWidth, int top, int bottom, int left, double scale)
        {
            int x = left;
            while (x < goldWidth && ColumnHasGold(gold, goldWidth, top, bottom, x))
                x++;
            if (x - left > 4 * scale)
                return left;
            int next = x;
            while (next < goldWidth && !ColumnHasGold(gold, goldWidth, top, bottom, next))
                next++;
            return next - x >= 4 * scale && next < goldWidth ? next : left;
        }

        private static bool ColumnHasGold(bool[] gold, int goldWidth, int top, int bottom, int x)
        {
            for (int y = top; y < bottom; y++)
            {
                if (gold[y * goldWidth + x])
                    return true;
            }
            return false;
        }

        // The last gold column of the name starting at left, a wider gap ends it
        private static int NameEnd(bool[] gold, int goldWidth, int top, int bottom, int left, int maxGap)
        {
            int end = left;
            for (int x = left; x < goldWidth && x - end <= maxGap; x++)
            {
                for (int y = top; y < bottom; y++)
                {
                    if (gold[y * goldWidth + x])
                    {
                        end = x;
                        break;
                    }
                }
            }
            return end;
        }

        // Red pixels between x0 and x1 in the rows of a band, the rightmost one and how many columns have any
        private static (int Count, int Edge, int Columns) CountRed(byte[] pixels, int width, int height, int top, int bottom, int x0, int x1)
        {
            int count = 0, edge = 0, columns = 0;
            for (int x = x0; x < Math.Min(x1, width); x++)
            {
                bool any = false;
                for (int y = Math.Max(top - 1, 0); y < Math.Min(bottom + 1, height); y++)
                {
                    int i = (y * width + x) * 4;
                    if (IsRed(pixels[i + 2], pixels[i + 1], pixels[i]))
                    {
                        count++;
                        edge = x;
                        any = true;
                    }
                }
                if (any)
                    columns++;
            }
            return (count, edge, columns);
        }

        // Follows the player's entries through the feed, entries only move up. An entry counts after showing in the given number of polls in a row,
        // once it has shown the same edge twice.
        internal class FeedTracker(int streak)
        {
            // Entries stay up as long as the player's killfeed duration setting says. Menus and the scoreboard can hide
            // the feed for a few seconds, a counted entry is remembered that long.
            private static readonly TimeSpan Hidden = TimeSpan.FromSeconds(5);
            private static readonly TimeSpan Dropout = TimeSpan.FromSeconds(1.5);
            // Entries slide in from below
            private const int SlideTolerance = 30;
            private const int EdgeTolerance = 6;
            // Rows are about 23 px apart
            private const int SameRowTolerance = 5, RowUp = 35;
            // Position alone only continues an entry seen in the last poll, a new entry that takes the slot of an expired one
            // shows after a gap
            private static readonly TimeSpan LastPoll = TimeSpan.FromMilliseconds(400);

            private readonly List<Entry> _entries = [];

            private class Entry
            {
                public int Y, Streak;
                // Scenery only lines up by position now and then, a real entry keeps its edge
                public bool EdgeMatched;
                public readonly List<int> Edges = [];

                public bool HasEdge(int edge) => Edges.Any(seen => Math.Abs(seen - edge) <= EdgeTolerance);
                public DateTime First, Last;
                public bool Counted;
            }

            // How well a row continues an entry, 0 when it can't
            private static int Score(FeedRow row, Entry entry, DateTime now)
            {
                if (row.Y > entry.Y + SlideTolerance || now - entry.Last > (entry.Counted ? Hidden : Dropout))
                    return 0;
                int distance = Math.Abs(entry.Y - row.Y);
                if (entry.HasEdge(row.Edge))
                    return 1000 - distance;
                // Stray pixels next to the names move the edge, an entry in the same row or one row below in the last poll is the same one
                if (row.Y <= entry.Y + SameRowTolerance && row.Y >= entry.Y - RowUp && now - entry.Last <= LastPoll)
                    return 500 - distance;
                return 0;
            }

            // Returns when each newly counted entry first showed
            public List<DateTime> Update(List<FeedRow> rows, DateTime now)
            {
                // Entries keep their order as they move up, so rows and entries are matched in order from the top
                var sortedRows = rows.OrderBy(row => row.Y).ToList();
                var entries = _entries.OrderBy(entry => entry.Y).ToList();
                var best = new int[sortedRows.Count + 1, entries.Count + 1];
                for (int i = 1; i <= sortedRows.Count; i++)
                {
                    for (int j = 1; j <= entries.Count; j++)
                    {
                        int score = Score(sortedRows[i - 1], entries[j - 1], now);
                        best[i, j] = Math.Max(Math.Max(best[i - 1, j], best[i, j - 1]), score > 0 ? best[i - 1, j - 1] + score : 0);
                    }
                }

                var matches = new Entry?[sortedRows.Count];
                for (int i = sortedRows.Count, j = entries.Count; i > 0 && j > 0;)
                {
                    int score = Score(sortedRows[i - 1], entries[j - 1], now);
                    if (score > 0 && best[i, j] == best[i - 1, j - 1] + score)
                        matches[--i] = entries[--j];
                    else if (best[i, j] == best[i - 1, j])
                        i--;
                    else
                        j--;
                }

                var counted = new List<DateTime>();
                var seen = new HashSet<Entry>();
                for (int i = 0; i < sortedRows.Count; i++)
                {
                    var row = sortedRows[i];
                    var entry = matches[i];
                    if (entry == null)
                    {
                        entry = new Entry { First = now };
                        _entries.Add(entry);
                    }
                    else if (entry.HasEdge(row.Edge))
                    {
                        entry.EdgeMatched = true;
                    }
                    seen.Add(entry);
                    entry.Y = row.Y;
                    if (!entry.Edges.Contains(row.Edge))
                        entry.Edges.Add(row.Edge);
                    entry.Last = now;
                    if (!entry.Counted && ++entry.Streak >= streak && entry.EdgeMatched)
                    {
                        entry.Counted = true;
                        counted.Add(entry.First);
                    }
                }

                foreach (var entry in _entries.Where(entry => !seen.Contains(entry) && !entry.Counted))
                    entry.Streak = 0;
                _entries.RemoveAll(entry => now - entry.Last > (entry.Counted ? Hidden : Dropout));
                return counted;
            }
        }
    }
}
