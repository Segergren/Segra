using Serilog;
using ObsKit.NET.Sources;
using Segra.Backend.Core.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace Segra.Backend.Games.TheFinals
{
    internal class TheFinalsIntegration : OcrIntegration
    {
        // Score popup below the crosshair, newest event on top ("ELIMINATION 250 + 250") and older ones listed below it.
        // Since late 2026 it can sit higher, so the region also covers the header.
        private static readonly CropRegion PopupRegion = new(X: 0.30, Y: 0.64, Width: 0.40, Height: 0.205);
        // "ELIMINATED X" header above the score lines, easier to read over bright scenery than the score lines
        private static readonly CropRegion HeaderRegion = new(X: 0.30, Y: 0.635, Width: 0.40, Height: 0.07);
        // Spectated teammates' kills show the header too, and the kill replay hides the death bar for up to 13 s
        private static readonly TimeSpan HeaderDeadTime = TimeSpan.FromSeconds(15);
        // Text of the crimson bar shown while dead, "ELIMINATED BY X" first and then respawn or revive countdowns
        private static readonly CropRegion DeathBarRegion = new(X: 0.25, Y: 0.93, Width: 0.50, Height: 0.06);
        // Binarizing loses the thin score lines over bright scenery, the bar text is white on solid red
        private const int PopupThreshold = 0;
        private const int BarThreshold = 150;

        // Over bright scenery a kill can read in only a few scattered polls
        private readonly PopupCounter _kills = new(requiredReads: 2, window: 5);
        private readonly DeathTracker _deaths = new();
        private DateTime _lastBar = DateTime.MinValue;

        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "THE FINALS",
            CropRegion = null,
            Keywords = [],
            // Windows OCR misses the small score lines at 1080p
            ReferenceHeight = 1440,
            TimeCompensation = TimeSpan.FromSeconds(0.5),
        };

        protected override async Task OnPoll(GameCapture source)
        {
            var now = DateTime.Now;
            var screenshot = source.TakeScreenshot(0, (uint)(source.Height * BarBandTop), 0, (uint)(source.Height * (1 - BarBandTop)));
            bool barVisible = screenshot != null && IsDeathBar(screenshot.Pixels, (int)screenshot.Width, (int)screenshot.Height);
            string barText = barVisible ? (await Recognize(source, DeathBarRegion, BarThreshold).ConfigureAwait(false))?.Text ?? "" : "";

            if (_deaths.Update(barVisible, barText, now) is DateTime deathTime)
            {
                AddBookmark(BookmarkType.Death, deathTime);
                Log.Information($"[THE FINALS] Detected death bar '{barText}' -> Death");
            }
            if (barVisible)
                _lastBar = now;

            var popup = await Recognize(source, PopupRegion, PopupThreshold).ConfigureAwait(false);
            var lines = popup?.Lines.Select(line => line.Text).ToList() ?? [];
            var (kills, visible) = CountPopup(lines);

            bool header = false;
            if (now - _lastBar > HeaderDeadTime)
                header = lines.Any(IsKillHeader) || IsKillHeader((await Recognize(source, HeaderRegion, PopupThreshold).ConfigureAwait(false))?.Text ?? "");
            // The header only shows the newest event, the score lines count multi-kills
            if (header)
                (kills, visible) = (Math.Max(kills, 1), true);

            for (int i = _kills.Update(kills, visible, header); i > 0; i--)
            {
                AddBookmark(BookmarkType.Kill);
                Log.Information($"[THE FINALS] Detected '{popup?.Text}' in score popup -> Kill");
            }
        }

        // Counts the popup's elimination lines, and whether the popup is showing at all
        private static (int Kills, bool Visible) CountPopup(IReadOnlyList<string> lines)
        {
            // Summary screens fill the region with stat tables
            if (lines.Count > 6)
                return (0, false);

            int kills = lines.Count(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(IsElimination));

            // Other events like "ASSIST 100" keep the popup up, and the running total ("+ 250") often reads when the event name does not
            bool visible = kills > 0 || lines.Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(IsAssist) || Regex.IsMatch(line, @"\+\s*\D?\d{2,}"));
            return (kills, visible);
        }

        // "ELIMINATED" (the header) and "ELIMINATIONS" (summary screens) don't end in ON.
        // The points often run into the word ("ELIMINATION250"), a single trailing digit is a misread S.
        private static bool IsElimination(string word)
        {
            var normalized = Normalize(word);
            var digits = normalized.Length - normalized.TrimEnd("0123456789".ToCharArray()).Length;
            if (digits >= 2)
                normalized = normalized[..^digits];
            return normalized.Length >= 8 && normalized.EndsWith("ON") && LevenshteinDistance(normalized, "ELIMINATION") <= 4;
        }

        // The header font's E often reads as a lowercase f ("fliMINATfD")
        private static bool IsKillHeader(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word =>
        {
            var normalized = Normalize(word.Replace('f', 'E'));
            return normalized.Length >= 7 && normalized.EndsWith('D') && LevenshteinDistance(normalized, "ELIMINATED") <= 3;
        });

        // Often read as "A5515T100", the points run into the word
        private static bool IsAssist(string word)
        {
            var normalized = Normalize(word).Replace('5', 'S').Replace('1', 'I').Replace('0', 'O');
            return normalized.Length >= 4 && normalized[0] == 'A' && LevenshteinDistance(normalized[..Math.Min(normalized.Length, 6)], "ASSIST") <= 2;
        }

        // Drops accents too, so other UI languages' "ELIMINACIÓN" reads like ELIMINATION
        private static string Normalize(string text) => new(text.Normalize(NormalizationForm.FormD)
            .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        // The bar is about (215, 25, 55) across the full width from 0.925 of the height down,
        // menus with the same red also fill the area above it
        private const double BarBandTop = 0.85;

        private static bool IsDeathBar(byte[] pixels, int w, int h)
        {
            double minBar = 1, sumBar = 0, maxAbove = 0;
            int barRows = 0;
            for (int i = 0; i < 20; i++)
            {
                // Rows from the band top (0.85) to the bottom edge
                double y = 0.85 + 0.15 * (i + 0.5) / 20;
                double share = RedShare(pixels, w, h, (int)((y - BarBandTop) / (1 - BarBandTop) * h));
                if (y < 0.922)
                    maxAbove = Math.Max(maxAbove, share);
                else if (y >= 0.94 && y < 0.993)
                {
                    minBar = Math.Min(minBar, share);
                    sumBar += share;
                    barRows++;
                }
            }
            return barRows > 0 && minBar >= 0.5 && sumBar / barRows >= 0.7 && maxAbove < 0.3;
        }

        private static double RedShare(byte[] pixels, int w, int h, int y)
        {
            y = Math.Clamp(y, 0, h - 1);
            int red = 0, total = 0;
            for (int x = 0; x < w; x += 4)
            {
                int i = (y * w + x) * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (Math.Abs(r - 209) < 30 && g < 70 && Math.Abs(b - 60) < 35)
                    red++;
                total++;
            }
            return (double)red / total;
        }

        private static bool IsRespawnText(string text)
        {
            // The bar font's E often reads as a lowercase f
            var normalized = Normalize(text.Replace('f', 'E')).Replace('5', 'S');
            return ContainsFuzzy(normalized, "RESPAWN", 2) || ContainsFuzzy(normalized, "REVIVE", 1);
        }

        private static bool ContainsFuzzy(string text, string keyword, int maxDistance)
        {
            for (int i = 0; i <= Math.Max(text.Length - keyword.Length, 0); i++)
            {
                if (LevenshteinDistance(text.Substring(i, Math.Min(keyword.Length, text.Length - i)), keyword) <= maxDistance)
                    return true;
            }
            return false;
        }

        // Popup lines stay up about 3 s and longer while new events keep coming
        private class PopupCounter(int requiredReads, int window)
        {
            // 1.5 s without any popup text ends the popup
            private const int QuietPollsToReset = 6;
            private readonly int[] _lastCounts = new int[window];
            private int _polls;
            private int _counted;
            private int _quietPolls;
            private bool _needsHeader;

            // Returns how many new lines appeared, a line has to be read in the required number of the last polls
            public int Update(int count, bool visible, bool header)
            {
                _lastCounts[_polls++ % _lastCounts.Length] = count;
                int seen = _lastCounts.OrderDescending().ElementAt(requiredReads - 1);

                _quietPolls = seen > 0 || visible ? 0 : _quietPolls + 1;
                if (_quietPolls >= QuietPollsToReset)
                    (_counted, _needsHeader) = (0, false);

                // Lines expire while bonuses keep the popup open, a new kill in their place comes with a new header
                if (_lastCounts.Max() < _counted)
                    (_counted, _needsHeader) = (_lastCounts.Max(), true);
                if (_needsHeader)
                {
                    if (!header)
                        return 0;
                    // Older lines can come back into view with it, only one line is new
                    (_counted, _needsHeader) = (Math.Max(_counted, seen - 1), false);
                }

                int added = Math.Max(seen - _counted, 0);
                _counted = Math.Max(_counted, seen);
                return added;
            }
        }

        // The bar hides during the kill replay, so it comes back with a respawn or revive countdown in the same death.
        // Modes without the replay (Super Cashball) start the bar on the countdown.
        private class DeathTracker
        {
            private static readonly TimeSpan NewBarGap = TimeSpan.FromSeconds(3);
            private static readonly TimeSpan ReplayGap = TimeSpan.FromSeconds(15);
            // Overlays like the scoreboard hide the bar for a few seconds
            private static readonly TimeSpan CountdownGap = TimeSpan.FromSeconds(10);
            private DateTime _lastBar = DateTime.MinValue;
            private DateTime? _firstSeen;
            private bool _fresh;
            private TimeSpan _gap;
            private bool? _lastWasCountdown;

            // Returns the death time once a new bar has shown "ELIMINATED BY" for 2 polls, or a countdown that doesn't continue the last death
            public DateTime? Update(bool barVisible, string text, DateTime now)
            {
                if (!barVisible)
                {
                    _firstSeen = null;
                    return null;
                }

                if (now - _lastBar > NewBarGap)
                {
                    _fresh = true;
                    _gap = now - _lastBar;
                }
                _lastBar = now;

                bool countdown = IsRespawnText(text);
                if (!_fresh)
                {
                    if (countdown)
                        _lastWasCountdown = true;
                    return null;
                }

                if (countdown)
                {
                    bool continues = _lastWasCountdown switch
                    {
                        false => _gap <= ReplayGap,
                        true => _gap <= CountdownGap,
                        null => false,
                    };
                    var start = _firstSeen ?? now;
                    _fresh = false;
                    _firstSeen = null;
                    _lastWasCountdown = true;
                    return continues ? null : start;
                }

                if (_firstSeen == null)
                {
                    _firstSeen = now;
                    return null;
                }

                _fresh = false;
                _lastWasCountdown = false;
                var deathTime = _firstSeen;
                _firstSeen = null;
                return deathTime;
            }
        }
    }
}
