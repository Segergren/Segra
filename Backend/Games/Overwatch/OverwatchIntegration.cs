using Serilog;
using ObsKit.NET.Sources;
using Segra.Backend.Core.Models;
using global::Windows.Media.Ocr;

namespace Segra.Backend.Games.Overwatch
{
    /// <summary>
    /// Detects eliminations and deaths from the notices below the crosshair, Overwatch has no log or API for them.
    ///
    /// The notices are boxes centered on the screen that stack downwards, newest on top, and stay up for about 2.5 s.
    /// They are found by color and measured against their own color, since brightness settings change how they look.
    /// The UI language does not matter:
    /// <list type="bullet">
    /// <item>Elimination: a red box with a skull and the enemy's name, the same count as the scoreboard's E column.
    /// Supports also get "ASSIST X" boxes, whose icon is a half lit face where the skull is symmetric. A box is followed
    /// by its width, which depends on the name in it. Destroying a deployable shows a longer box ("ELIMINATED X'S
    /// TURRET"), so boxes wider than any name are read with OCR and skipped when they say so. Status notices ("ORB OF
    /// DISCORD FROM X") have slanted ends.</item>
    /// <item>Kill or assist: an elimination is also given for enemies a teammate finishes. The kill feed shows who got
    /// the final blow, so it is read with OCR along with the player's name under their health bar.</item>
    /// <item>Death: a grey box with a warning sign and dark text, "YOU WERE ELIMINATED BY X". Dying to the environment
    /// shows a shorter "YOU HAVE DIED", which is as wide as "PERK EQUIPPED!", so short grey boxes are read with OCR.</item>
    /// </list>
    ///
    /// After a death the kill cam replays the killer's view with their notices, our own name among them, and then the
    /// player spectates a teammate. Both put a dark bar with a thin light line at the top of the screen, and nothing is
    /// counted while it shows. It also counts as a death when no death notice was seen before it, which happens when
    /// the grey box blends into a bright wall. Play of the Game after the match has a taller bar.
    ///
    /// Positions are scaled by the screen height, the notices are measured from the screen center.
    /// </summary>
    internal class OverwatchIntegration : OcrIntegration
    {
        // Notice area at 1080p, centered horizontally
        private const int AreaWidth = 800, AreaTop = 660, AreaHeight = 280;
        // Top of the first notice and the distance between stacked notices. Notices sit at 753 in older versions and
        // around 767 in newer ones, which also add the player's share of the elimination after the name.
        // The ultimate meter sits where a fifth notice would.
        private const int SlotTop = 760, SlotHeight = 36, Slots = 4;
        // Red boxes wider than a name can be a destroyed deployable, grey boxes narrower than a death with a killer can be a perk
        private const int NameBoxMax = 260, DeathBoxMin = 260, DeathBoxMax = 600, SmallBoxMin = 150;
        // Skull, assist face or warning sign at the left of a box
        private const int IconWidth = 40;
        // Top bar while dead: the dark bar and the light line below it, which can sit a few pixels higher
        private const int BarTop = 125, LineTop = 143, LineBottom = 157;
        // The Play of the Game bar ends higher
        private const int ReplayBarTop = 100, ReplayLineTop = 125, ReplayLineBottom = 132;
        // The player's name next to their rank under the health bar, from the left edge
        private const int NameLeft = 140, NameTop = 950, NameWidth = 380, NameHeight = 60;
        // Kill feed rows from the right edge, lower in Stadium. A victim's name ends at the edge, an attacker's well before it.
        private const int FeedWidth = 560, FeedTop = 30, FeedHeight = 300, AttackerEnd = 480;
        // Names are white, but dimmer on some HUDs and video settings
        private const int BrightText = 170, DimText = 130, ReferenceHeight = 2160;

        private static readonly TimeSpan KillCamHold = TimeSpan.FromSeconds(2);
        // The kill cam starts 1-7 s after the death
        private static readonly TimeSpan KillCamDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan KillCamDeathGap = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan DeathPollGap = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DeathGap = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan SmallBoxReuse = TimeSpan.FromSeconds(3);
        // Kill feed rows stay up for about 7.5 s
        private static readonly TimeSpan FeedRowLifetime = TimeSpan.FromSeconds(8);

        private readonly NoticeTracker _kills = new();
        // Reads of the player's name, misreads and replays of a teammate's view are outvoted
        private readonly List<string> _names = [];
        private readonly List<DateTime> _finalBlows = [];
        // How far notices sit from the slots, learned from counted eliminations
        private readonly List<int> _slotOffsets = [];
        private DateTime _killCamFirst, _killCamSeen, _replaySeen, _deathFirst, _deathLast, _deathBookmarked;
        private bool _barLastPoll;
        private int _deathPolls;
        private DateTime _smallBoxRead;
        private bool _smallBoxIsDeath;

        private readonly record struct Box(bool Red, int Top, int Bottom, int Width, int Spread, double Icon, double Symmetry, double Text, double Dark)
        {
            public int Slot => (int)Math.Round((Top - SlotTop) / (double)SlotHeight);
            public int Offset => Top - SlotTop - Slot * SlotHeight;
            // A notice is about 30 px tall, the rows with its edges and text found are 14 to 32
            public bool InSlot => Bottom - Top is >= 14 and <= 32 && Slot is >= 0 and < Slots && Math.Abs(Offset) <= 14;
            public bool Elimination => Red && InSlot && Text >= 0.08 && Icon >= 0.2 && Symmetry >= 0.7 && Spread <= 12;
            // Later notices push it down at most a slot or two, lower boxes are scenery like white sleeves. The warning
            // sign tells it apart from the grey "HOLD F1 TO VIEW HERO DETAILS" prompt after picking a hero.
            public bool Death => !Red && InSlot && Slot <= 2 && Icon >= 0.2 && Dark is >= 0.1 and <= 0.4 && Spread <= 6 && Width is >= SmallBoxMin and <= DeathBoxMax;
        }

        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "Overwatch",
            CropRegion = null,
            Keywords = [],
            Threshold = 0,
            ReferenceHeight = ReferenceHeight,
            // Notices show with the elimination, the first poll that sees one is 0-250 ms later
            TimeCompensation = TimeSpan.FromSeconds(0.1),
        };

        protected override async Task OnPoll(GameCapture source)
        {
            var now = DateTime.Now;
            double scale = source.Height / 1080.0;
            var strip = source.TakeScreenshot(0, (uint)(ReplayBarTop * scale), source.Width, (uint)((LineBottom - ReplayBarTop) * scale));
            bool bar = strip != null && EdgeShare(strip, scale, BarTop, LineTop, LineBottom) >= 0.6;
            if (bar)
            {
                // A bright beam can cross the line for a moment, so a death needs the bar on two polls in a row
                if (now - _killCamSeen > KillCamHold)
                    _killCamFirst = now;
                else if (_barLastPoll && _killCamFirst - _deathBookmarked > KillCamDeathGap)
                    AddDeath(_killCamFirst - KillCamDelay, "kill cam");
                _killCamSeen = now;
            }
            _barLastPoll = bar;
            if (strip != null && EdgeShare(strip, scale, ReplayBarTop, ReplayLineTop, ReplayLineBottom) >= 0.6)
                _replaySeen = now;
            if (now - _killCamSeen <= KillCamHold || now - _replaySeen <= KillCamHold)
                return;

            uint left = (uint)Math.Max(source.Width / 2.0 - AreaWidth / 2 * scale, 0);
            var screenshot = source.TakeScreenshot(left, (uint)(AreaTop * scale), (uint)(AreaWidth * scale), (uint)(AreaHeight * scale));
            if (screenshot == null)
                return;

            var boxes = ReadNotices(screenshot, scale);

            foreach (var box in boxes.Where(box => box.Elimination))
            {
                var entry = _kills.Track(box.Width, box.Top, now);
                if (entry.Polls < 2 || entry.Counted)
                    continue;

                entry.Counted = true;
                if (_kills.SlidIn(entry, now))
                    continue;

                _slotOffsets.Add(box.Offset);
                if (_slotOffsets.Count > 5)
                    _slotOffsets.RemoveAt(0);
                if (box.Width > NameBoxMax && await ContainsAsync(source, scale, box, "ELIMINATED").ConfigureAwait(false))
                    continue;

                // Without a readable name every elimination stays a kill
                var player = await ReadPlayerAsync(source, scale).ConfigureAwait(false);
                var type = player == null || await IsFinalBlowAsync(source, scale, player, now).ConfigureAwait(false) ? BookmarkType.Kill : BookmarkType.Assist;
                AddBookmark(type, entry.First);
                Log.Information($"[Overwatch] Detected elimination notice -> {type}");
            }
            _kills.Expire(now);

            // Scenery forms grey boxes too, the death notice lines up with the slots like the eliminations do
            int slotOffset = _slotOffsets.Count == 0 ? 0 : _slotOffsets.Order().ElementAt(_slotOffsets.Count / 2);
            var death = boxes.FirstOrDefault(box => box.Death && Math.Abs(box.Offset - slotOffset) <= 7);
            if (death.Width == 0)
                return;

            if (death.Width < DeathBoxMin)
            {
                if (now - _smallBoxRead > SmallBoxReuse)
                    _smallBoxIsDeath = await ContainsAsync(source, scale, death, "DIED").ConfigureAwait(false);
                _smallBoxRead = now;
                if (!_smallBoxIsDeath)
                    return;
            }

            if (now - _deathLast > DeathPollGap)
            {
                _deathFirst = now;
                _deathPolls = 0;
            }
            _deathLast = now;
            if (++_deathPolls == 2 && _deathFirst - _deathBookmarked > DeathGap)
                AddDeath(_deathFirst, "death notice");
        }

        private void AddDeath(DateTime time, string source)
        {
            _deathBookmarked = time;
            AddBookmark(BookmarkType.Death, time);
            Log.Information($"[Overwatch] Detected {source} -> Death");
        }

        // Share of columns where the light line is clearly brighter than the dark bar above it, in a strip from ReplayBarTop
        private static double EdgeShare(ScreenshotData strip, double scale, int barTop, int lineTop, int lineBottom)
        {
            int width = (int)strip.Width, height = (int)strip.Height;
            int step = Math.Max((int)(8 * scale), 1);
            int barStart = (int)((barTop - ReplayBarTop) * scale), barEnd = (int)((lineTop - 2 - ReplayBarTop) * scale);
            int lineStart = (int)((lineTop - ReplayBarTop) * scale), lineEnd = Math.Min((int)((lineBottom - ReplayBarTop) * scale), height);
            int columns = 0, edges = 0;
            for (int x = 0; x < width; x += step)
            {
                columns++;
                if (MaxLuma(strip.Pixels, width, x, lineStart, lineEnd) - MaxLuma(strip.Pixels, width, x, barStart, barEnd) >= 40)
                    edges++;
            }
            return columns == 0 ? 0 : (double)edges / columns;
        }

        private static int MaxLuma(byte[] pixels, int width, int x, int fromRow, int toRow)
        {
            int max = 0;
            for (int row = fromRow; row < toRow; row++)
            {
                int i = (row * width + x) * 4;
                max = Math.Max(max, (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3);
            }
            return max;
        }

        // Finds the boxes centered in the notice area, measured in 1080p pixels
        private static List<Box> ReadNotices(ScreenshotData screenshot, double scale)
        {
            // Resample to 1080p so every size below is in 1080p pixels
            int sw = (int)screenshot.Width, sh = (int)screenshot.Height;
            var rgb = new byte[AreaWidth * AreaHeight * 3];
            for (int y = 0; y < AreaHeight; y++)
            {
                int sy = Math.Min((int)(y * scale), sh - 1);
                for (int x = 0; x < AreaWidth; x++)
                {
                    int si = (sy * sw + Math.Min((int)(x * scale), sw - 1)) * 4, di = (y * AreaWidth + x) * 3;
                    rgb[di] = screenshot.Pixels[si + 2];
                    rgb[di + 1] = screenshot.Pixels[si + 1];
                    rgb[di + 2] = screenshot.Pixels[si];
                }
            }

            var boxes = new List<Box>();
            // Red is transparent over the scene, so it is a hue rather than a color
            boxes.AddRange(FindBoxes(rgb, true, (r, g, b) => r >= 100 && r - g >= 50 && r - b >= 30));
            boxes.AddRange(FindBoxes(rgb, false, (r, g, b) => r + g + b >= 360 && Math.Abs(r - g) <= 20 && Math.Abs(g - b) <= 20));
            return boxes;
        }

        private static IEnumerable<Box> FindBoxes(byte[] rgb, bool red, Func<int, int, int, bool> color)
        {
            var mask = new bool[AreaWidth * AreaHeight];
            for (int i = 0; i < mask.Length; i++)
                mask[i] = color(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);

            // A row of a box: the outermost pair of edges centered on the screen, mostly filled between them
            var rows = new List<(int Y, int Left, int Right)>();
            var edges = new List<(int Start, int End)>();
            for (int y = 0; y < AreaHeight; y++)
            {
                edges.Clear();
                int start = -1, last = -10;
                for (int x = 0; x <= AreaWidth; x++)
                {
                    bool on = x < AreaWidth && mask[y * AreaWidth + x];
                    if (on && x - last > 2)
                    {
                        if (start >= 0 && last - start >= 3)
                            edges.Add((start, last));
                        start = x;
                    }
                    if (on)
                        last = x;
                }
                if (start >= 0 && last - start >= 3)
                    edges.Add((start, last));

                var row = OutermostCentered(mask, y, edges);
                if (row != null)
                    rows.Add((y, row.Value.Left, row.Value.Right));
            }

            // Consecutive rows of the same width make a box, a notice is about 30 rows tall
            int first = 0;
            for (int i = 1; i <= rows.Count; i++)
            {
                if (i < rows.Count && rows[i].Y - rows[i - 1].Y <= 2
                    && Math.Abs(rows[i].Right - rows[i].Left - (rows[i - 1].Right - rows[i - 1].Left)) <= 12)
                    continue;

                if (i - first >= 12)
                    yield return Measure(rgb, mask, red, rows.GetRange(first, i - first));
                first = i;
            }
        }

        private static (int Left, int Right)? OutermostCentered(bool[] mask, int y, List<(int Start, int End)> edges)
        {
            for (int a = 0; a < edges.Count; a++)
            {
                for (int b = edges.Count - 1; b > a; b--)
                {
                    int left = edges[a].Start, right = edges[b].End;
                    if (right - left < 60 || Math.Abs((left + right) / 2.0 - AreaWidth / 2) > 14)
                        continue;

                    int filled = 0;
                    for (int x = left; x <= right; x++)
                        filled += mask[y * AreaWidth + x] ? 1 : 0;
                    if (filled * 4 >= right - left + 1)
                        return (left, right);
                }
            }
            return null;
        }

        // Shares of pixels clearly brighter than the box in the icon (darker for a grey box) and the text part, how evenly
        // the icon spreads around its middle, and the share of pixels clearly darker than the box in the text part
        private static Box Measure(byte[] rgb, bool[] mask, bool red, List<(int Y, int Left, int Right)> rows)
        {
            int left = rows.Select(row => row.Left).Order().ElementAt(rows.Count / 2);
            int right = rows.Select(row => row.Right).Order().ElementAt(rows.Count / 2);
            var widths = rows.Select(row => row.Right - row.Left).Order().ToList();
            int width = widths[rows.Count / 2], spread = widths[rows.Count * 9 / 10] - widths[rows.Count / 10];
            int top = rows[0].Y, bottom = rows[^1].Y;

            // The box's own brightness: the median of its colored pixels
            var histogram = new int[256];
            int boxPixels = 0;
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    if (!mask[y * AreaWidth + x])
                        continue;
                    histogram[Luma(rgb, y, x)]++;
                    boxPixels++;
                }
            }
            int boxLuma = 0;
            for (int seen = 0; boxLuma < 255 && (seen += histogram[boxLuma]) * 2 < boxPixels; boxLuma++) { }

            int icon = 0, iconTotal = 0, text = 0, dark = 0, textTotal = 0, iconLeft = int.MaxValue, iconRight = -1;
            var iconColumns = new int[IconWidth];
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    int luma = Luma(rgb, y, x);
                    bool bright = luma >= boxLuma + 60;
                    if (x < left + IconWidth)
                    {
                        iconTotal++;
                        if (red ? !bright : luma > boxLuma - 50)
                            continue;
                        icon++;
                        iconColumns[x - left]++;
                        iconLeft = Math.Min(iconLeft, x - left);
                        iconRight = Math.Max(iconRight, x - left);
                    }
                    else
                    {
                        textTotal++;
                        text += bright ? 1 : 0;
                        dark += luma <= boxLuma - 50 ? 1 : 0;
                    }
                }
            }

            double symmetry = 0;
            if (iconRight >= 0)
            {
                double middle = (iconLeft + iconRight) / 2.0;
                int before = 0, after = 0;
                for (int x = iconLeft; x <= iconRight; x++)
                {
                    if (x < middle)
                        before += iconColumns[x];
                    else if (x > middle)
                        after += iconColumns[x];
                }
                symmetry = (double)Math.Min(before, after) / Math.Max(Math.Max(before, after), 1);
            }

            return new Box(red, top + AreaTop, bottom + AreaTop, width, spread, (double)icon / Math.Max(iconTotal, 1), symmetry,
                (double)text / Math.Max(textTotal, 1), (double)dark / Math.Max(textTotal, 1));
        }

        private static int Luma(byte[] rgb, int y, int x)
        {
            int i = (y * AreaWidth + x) * 3;
            return (rgb[i] + rgb[i + 1] + rgb[i + 2]) / 3;
        }

        private async Task<bool> ContainsAsync(GameCapture source, double scale, Box box, string word)
        {
            double x = source.Width / 2.0 - box.Width / 2.0 * scale, y = (box.Top - 4) * scale;
            var crop = new CropRegion(x / source.Width, y / source.Height, box.Width * scale / source.Width, 36 * scale / source.Height);
            var result = await Recognize(source, crop, 0).ConfigureAwait(false);
            return result != null && FuzzyContains(result.Text, word);
        }

        // The player's rows in the kill feed beyond the final blows counted while those rows can still be up are new
        // Reads the name under the health bar, the player's is the read most others agree with
        private async Task<string?> ReadPlayerAsync(GameCapture source, double scale)
        {
            var region = Region(source, scale, NameLeft, NameTop, NameWidth, NameHeight);
            var name = PickName(await Recognize(source, region, BrightText).ConfigureAwait(false))
                       ?? PickName(await Recognize(source, region, DimText).ConfigureAwait(false));
            if (name != null)
            {
                _names.Add(name);
                if (_names.Count > 50)
                    _names.RemoveAt(0);
            }
            // Reads a letter or two apart are the same name
            return Enumerable.Reverse(_names).MaxBy(read => _names.Count(other => SameName(other, read)));
        }

        private async Task<bool> IsFinalBlowAsync(GameCapture source, double scale, string player, DateTime now)
        {
            _finalBlows.RemoveAll(time => now - time > FeedRowLifetime);
            var feedRegion = Region(source, scale, source.Width / scale - FeedWidth, FeedTop, FeedWidth, FeedHeight);
            foreach (int threshold in (int[])[BrightText, DimText])
            {
                var feed = await Recognize(source, feedRegion, threshold).ConfigureAwait(false);
                if (feed != null && CountAttackerRows(feed, player) > _finalBlows.Count)
                {
                    _finalBlows.Add(now);
                    return true;
                }
            }
            return false;
        }

        private static CropRegion Region(GameCapture source, double scale, double left, double top, double width, double height) =>
            new(left * scale / source.Width, top * scale / source.Height, width * scale / source.Width, height * scale / source.Height);

        // The only word next to the rank number, prompts like "LALT SELECT PERK" take its place at times
        private static string? PickName(OcrResult? result)
        {
            var words = result?.Lines.SelectMany(line => line.Words).Select(word => word.Text.ToUpperInvariant())
                .Where(text => text.Length >= 3 && text.Any(char.IsLetter) && text.All(char.IsLetterOrDigit)).ToList();
            return words?.Count == 1 ? words[0] : null;
        }

        // Rows where the player's name ends well before the right edge, where the victim's name is
        private static int CountAttackerRows(OcrResult feed, string player)
        {
            double attackerEnd = AttackerEnd * ReferenceHeight / 1080.0;
            return feed.Lines.SelectMany(line => line.Words).Count(word =>
                word.BoundingRect.X + word.BoundingRect.Width <= attackerEnd && SameName(word.Text, player));
        }

        private static bool SameName(string text, string player) =>
            text.Contains(player, StringComparison.OrdinalIgnoreCase) ||
            LevenshteinDistance(text.ToUpperInvariant(), player) <= (player.Length >= 10 ? 2 : player.Length >= 5 ? 1 : 0);

        // Follows notices by their width while they stack and move down
        private class NoticeTracker
        {
            private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(3.5);
            private static readonly TimeSpan Dropout = TimeSpan.FromSeconds(1);
            // A notice can slide in wider than it settles, in the same place, and be followed as another one for a moment
            private static readonly TimeSpan SlideIn = TimeSpan.FromSeconds(1), SlideInSeen = TimeSpan.FromSeconds(0.3);

            private readonly List<Entry> _entries = [];
            private readonly HashSet<Entry> _seen = [];
            private DateTime _poll;

            public class Entry
            {
                public int Width, Top, Polls;
                public DateTime First, Last;
                public bool Counted;
            }

            public Entry Track(int width, int top, DateTime now)
            {
                if (now != _poll)
                {
                    _seen.Clear();
                    _poll = now;
                }

                var entry = _entries.Where(entry => !_seen.Contains(entry) && Math.Abs(entry.Width - width) <= 6
                                                    && (now - entry.Last <= Dropout || now - entry.First <= Lifetime))
                                    .MinBy(entry => Math.Abs(entry.Width - width));
                if (entry == null)
                {
                    entry = new Entry { Width = width, First = now };
                    _entries.Add(entry);
                }
                _seen.Add(entry);
                entry.Top = top;
                entry.Last = now;
                entry.Polls++;
                return entry;
            }

            // A counted notice seen only briefly just before, a bit wider in the same place
            public bool SlidIn(Entry entry, DateTime now) =>
                _entries.Any(other => other != entry && other.Counted && other.Last != now && other.Last - other.First <= SlideInSeen
                                      && other.First < entry.First && entry.First - other.First <= SlideIn
                                      && Math.Abs(other.Top - entry.Top) <= 6 && other.Width - entry.Width is > 0 and <= 24);

            public void Expire(DateTime now) =>
                _entries.RemoveAll(entry => now - entry.Last > Dropout && now - entry.First > Lifetime);
        }
    }
}
