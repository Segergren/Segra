using Serilog;
using System.Text;
using ObsKit.NET.Sources;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.Valorant
{
    /// <summary>
    /// Detects kills and deaths from the kill feed in the top right, Valorant has no log or API for them.
    ///
    /// Every kill feed entry with the local player has a yellow outline around the player's portrait. It is found by
    /// color, so the player's name is not needed:
    /// <list type="bullet">
    /// <item>Kill: the yellow portrait is on the left, the killer's side, and the victim's name box is red for an enemy.
    /// Teamkills and the spike killing the player have the team's teal there.</item>
    /// <item>Death: the yellow portrait is on the right, the victim's side, and the KILLED BY header of the combat report
    /// shows around the same time. Phoenix dying during Run It Back shows the entry without the header, and the game
    /// does not count it as a death. Entries with the player on both sides (the spike, Clove's ultimate) are skipped too.</item>
    /// </list>
    ///
    /// Entries stay up for 5 s, new ones are added at the bottom and older ones move up. A kill entry is followed by the
    /// distance of its left edge from the right side of the screen, which depends on the names and weapon in it.
    /// Entries count after showing in 4 polls in a row. Death entries are followed by their row, so Clove dying again
    /// after her ultimate counts twice.
    ///
    /// Positions are measured from the top right corner and scaled by the screen height.
    /// Only the English UI is supported for deaths.
    ///
    /// The screen is not read while the game's log says it is in the main menu.
    /// </summary>
    internal class ValorantIntegration : OcrIntegration
    {
        // Kill feed at 1080p: top of the first entry, distance between entries and how many rows are read
        private const int RowTop = 96, RowHeight = 39, Rows = 5;
        // Read area, measured from the right edge, wide enough for entries with two long names
        private const int FeedWidth = 800, FeedTop = 76, FeedBottom = 300;
        // Yellow edges further from the right edge than this are the killer's portrait, closer than VictimOffset the victim's
        private const int KillerOffset = 134, VictimOffset = 96;
        // Victim's name box, left of the victim's portrait
        private const int NameBoxLeft = 134, NameBoxRight = 80;

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Logs", "ShooterGame.log");
        private const string StateLine = "Reconcile called with the current state: ";

        private readonly FeedTracker _feed = new();
        private DateTime _panelSeen;
        private long _logOffset;
        private bool _inMenu;

        private readonly record struct FeedRow(int KillerOffset, bool EnemyVictim, bool PlayerDied);

        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "Valorant",
            // KILLED BY header of the combat report, which moves with the report's height
            CropRegion = new CropRegion(X: 0.80, Y: 0.10, Width: 0.15, Height: 0.40),
            Keywords = [],
            Threshold = 0,
            ReferenceHeight = 2160,
            // An entry shows within a few frames of the kill and is usually caught one poll later
            TimeCompensation = TimeSpan.FromSeconds(0.3),
        };

        // InGame and other states resume the reads, so a missing log or changed lines keep them going
        protected override bool ShouldPoll()
        {
            try
            {
                (_logOffset, var state) = ReadState(LogPath, _logOffset);
                if (state != null && state == "MainMenu" != _inMenu)
                {
                    _inMenu = state == "MainMenu";
                    Log.Information(_inMenu ? "[Valorant] In the main menu, pausing screen reads" : $"[Valorant] Game state {state}, reading the screen");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            return !_inMenu;
        }

        // Returns the offset to read from next time and the last game state logged after the given offset
        private static (long Offset, string? State) ReadState(string path, long offset)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            // Each game launch starts a new log
            if (length < offset)
                offset = 0;

            var bytes = new byte[length - offset];
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(bytes);

            // A line that is still being written is read again next time
            int end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            var text = Encoding.UTF8.GetString(bytes, 0, end);
            int index = text.LastIndexOf(StateLine, StringComparison.Ordinal);
            if (index < 0)
                return (offset + end, null);

            // "...current state: MainMenu. No action taken."
            int start = index + StateLine.Length, stop = start;
            while (stop < text.Length && char.IsLetter(text[stop]))
                stop++;
            return (offset + end, text[start..stop]);
        }

        protected override void ProcessText(string text)
        {
            if (text.Contains("KILLED BY", StringComparison.OrdinalIgnoreCase))
                _panelSeen = DateTime.Now;
        }

        protected override Task OnPoll(GameCapture source)
        {
            double scale = source.Height / 1080.0;
            uint left = (uint)Math.Max(source.Width - FeedWidth * scale, 0);
            var screenshot = source.TakeScreenshot(left, (uint)(FeedTop * scale), source.Width - left, (uint)((FeedBottom - FeedTop) * scale));
            if (screenshot == null)
                return Task.CompletedTask;

            var rows = ReadFeed(screenshot.Pixels, (int)screenshot.Width, (int)screenshot.Height, scale);
            if (rows == null)
                return Task.CompletedTask;

            foreach (var time in _feed.Update(rows, DateTime.Now))
            {
                AddBookmark(BookmarkType.Kill, time);
                Log.Information("[Valorant] Detected kill feed entry -> Kill");
            }

            foreach (var time in _feed.ConfirmDeaths(_panelSeen))
            {
                AddBookmark(BookmarkType.Death, time);
                Log.Information("[Valorant] Detected kill feed entry and 'KILLED BY' -> Death");
            }
            return Task.CompletedTask;
        }

        // Finds the player's portrait edges in each kill feed row, null when a yellow flash covers the feed
        private static FeedRow[]? ReadFeed(byte[] pixels, int width, int height, double scale)
        {
            var yellow = new bool[width * height];
            int count = 0;
            for (int i = 0; i < yellow.Length; i++)
            {
                int b = pixels[i * 4], g = pixels[i * 4 + 1], r = pixels[i * 4 + 2];
                // Pale yellow, Killjoy's portrait is a deeper yellow with less blue
                if (r >= 170 && g >= 170 && b >= 70 && g >= r - 15 && Math.Min(r, g) - b >= 50)
                {
                    yellow[i] = true;
                    count++;
                }
            }
            if (count > yellow.Length / 25)
                return null;

            var killers = new int[Rows];
            var victims = new bool[Rows];
            for (int x = 0; x < width; x++)
            {
                int offset = (int)((width - x) / scale);
                int y = 0;
                while (y < height)
                {
                    if (!yellow[y * width + x])
                    {
                        y++;
                        continue;
                    }

                    int top = y;
                    while (y < height && yellow[y * width + x])
                        y++;

                    // A portrait edge spans most of the entry's height, assist icons are smaller
                    double length = (y - top) / scale;
                    double rowTop = top / scale + FeedTop - RowTop;
                    int row = (int)Math.Round(rowTop / RowHeight);
                    if (length < 20 || length > 40 || row < 0 || row >= Rows || Math.Abs(rowTop - row * RowHeight) > 4)
                        continue;

                    if (offset > KillerOffset)
                        killers[row] = Math.Max(killers[row], offset);
                    else if (offset <= VictimOffset)
                        victims[row] = true;
                }
            }

            var rows = new FeedRow[Rows];
            for (int row = 0; row < Rows; row++)
            {
                // Name box color: red for an enemy, teal for the player's team
                int red = 0, teal = 0, total = 0;
                int y0 = (int)((RowTop + row * RowHeight + 8 - FeedTop) * scale), y1 = (int)((RowTop + row * RowHeight + 26 - FeedTop) * scale);
                int x0 = (int)(width - NameBoxLeft * scale), x1 = (int)(width - NameBoxRight * scale);
                for (int y = Math.Max(y0, 0); y < Math.Min(y1, height); y++)
                {
                    for (int x = Math.Max(x0, 0); x < x1; x++)
                    {
                        int i = (y * width + x) * 4;
                        int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                        if (r >= 150 && r - g >= 60 && r - b >= 50)
                            red++;
                        else if (g >= 120 && g - r >= 40)
                            teal++;
                        total++;
                    }
                }
                // Long names fill most of the box with white text
                bool enemy = red > teal && red * 20 >= total;
                bool team = teal > red && teal * 20 >= total * 3;
                // The player on both sides is the spike or Clove's ultimate, not a death on the scoreboard
                rows[row] = new FeedRow(killers[row], enemy, victims[row] && team && killers[row] == 0);
            }
            return rows;
        }

        // Follows the player's entries through the feed, entries only move up so a match is in the same or a higher row
        private class FeedTracker
        {
            private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5.25);
            private static readonly TimeSpan Dropout = TimeSpan.FromSeconds(1);
            // The KILLED BY header can show just before or a little after the death entry, and players can close it right away
            private static readonly TimeSpan PanelBefore = TimeSpan.FromSeconds(2), PanelAfter = TimeSpan.FromSeconds(5);
            // Polls in a row an entry needs, Killjoy's portrait and yellow scenery can show edges for a few
            private const int Streak = 4;

            private readonly List<Entry> _kills = [];
            private readonly List<Entry> _deaths = [];

            private class Entry
            {
                public int Offset, Row, Streak;
                public DateTime First, Last;
                public bool Counted, Confirmed;
            }

            // Returns when each newly counted kill's entry first showed
            public List<DateTime> Update(FeedRow[] rows, DateTime now)
            {
                var kills = new List<DateTime>();
                var seen = new HashSet<Entry>();
                for (int row = 0; row < rows.Length; row++)
                {
                    if (rows[row].KillerOffset > 0)
                    {
                        // A kill entry is followed by its left edge, which depends on the names and weapon in it
                        var entry = Follow(_kills, seen, row, rows[row].KillerOffset, now);
                        entry.Streak = rows[row].EnemyVictim ? entry.Streak + 1 : 0;
                        if (!entry.Counted && entry.Streak >= Streak)
                        {
                            entry.Counted = true;
                            kills.Add(entry.First);
                        }
                    }

                    if (rows[row].PlayerDied)
                    {
                        var entry = Follow(_deaths, seen, row, 0, now);
                        if (++entry.Streak >= Streak)
                            entry.Counted = true;
                    }
                }

                foreach (var entry in _kills.Concat(_deaths).Where(entry => !seen.Contains(entry)))
                    entry.Streak = 0;
                // A death entry that did not show in a row from its start was a new entry's flash in another row
                _deaths.RemoveAll(entry => (!entry.Counted && entry.Streak == 0) || (now - entry.Last > Dropout && now - entry.First > Lifetime));
                _kills.RemoveAll(entry => now - entry.Last > Dropout && now - entry.First > Lifetime);
                return kills;
            }

            // Returns when each death entry first showed, once the KILLED BY header has been read around it
            public List<DateTime> ConfirmDeaths(DateTime panelSeen)
            {
                var deaths = new List<DateTime>();
                foreach (var entry in _deaths.Where(entry => entry.Counted && !entry.Confirmed
                                                             && panelSeen >= entry.First - PanelBefore && panelSeen <= entry.First + PanelAfter))
                {
                    entry.Confirmed = true;
                    deaths.Add(entry.First);
                }
                return deaths;
            }

            // Established entries first, a new entry's flash can show yellow in another row
            private static Entry Follow(List<Entry> entries, HashSet<Entry> seen, int row, int offset, DateTime now)
            {
                var entry = entries
                    .Where(entry => !seen.Contains(entry) && Math.Abs(entry.Offset - offset) <= 6 && row <= entry.Row
                                    && (now - entry.Last <= Dropout || now - entry.First <= Lifetime))
                    .OrderByDescending(entry => entry.Counted)
                    .FirstOrDefault();

                // Yellow scenery left of an entry moves its left edge. A row only gets a new entry after the one in it
                // has moved up or expired, so a young entry in the same row is the same one.
                if (entry == null)
                {
                    entry = entries.FirstOrDefault(entry => !seen.Contains(entry) && entry.Row == row && now - entry.Last <= Dropout
                                                            && now - entry.First <= Lifetime - Dropout);
                    if (entry != null)
                        entry.Offset = offset;
                }

                if (entry == null)
                {
                    entry = new Entry { Offset = offset, First = now };
                    entries.Add(entry);
                }
                seen.Add(entry);
                entry.Row = row;
                entry.Last = now;
                return entry;
            }
        }
    }
}
