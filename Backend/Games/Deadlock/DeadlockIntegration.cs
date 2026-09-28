using Serilog;
using ObsKit.NET.Sources;
using Segra.Backend.Core.Models;
using global::Windows.Media.Ocr;
using System.Text.RegularExpressions;

namespace Segra.Backend.Games.Deadlock
{
    internal class DeadlockIntegration : OcrIntegration
    {
        // Hero badges above the crosshair get a white "KILL" or "KILL ASSIST" stamp, tilted 27 degrees.
        // Starts below the objective health bars and streak banners, which confuse the OCR.
        private static readonly CropRegion StampRegion = new(X: 0.35, Y: 0.195, Width: 0.30, Height: 0.075);
        private const float StampAngle = 27;
        // Keeps only the white text, the assist badge itself is bright yellow
        private const int StampThreshold = 230;

        private readonly StampCounter _kills = new();
        private readonly StampCounter _assists = new();
        private readonly int[] _recentAssists = new int[5];
        private int _polls;
        private DateTime _deadUntil;

        protected override OcrConfig GetConfig() => new()
        {
            LogPrefix = "Deadlock",
            // "Respawn in: N" box, handled in ProcessText
            CropRegion = new CropRegion(X: 0.40, Y: 0.72, Width: 0.20, Height: 0.08),
            Keywords = [],
            Threshold = 0,
            ReferenceHeight = 1440,
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
            var result = await Recognize(source, StampRegion, StampThreshold, StampAngle).ConfigureAwait(false);
            if (result == null)
                return;

            var (kills, assists) = CountStamps(result);
            // The ASSIST half of a stamp can go unread for a few polls, a KILL right after an assist is that stamp
            _recentAssists[_polls++ % _recentAssists.Length] = assists;
            kills = Math.Max(kills + assists - _recentAssists.Max(), 0);

            var now = DateTime.UtcNow;
            for (int i = _kills.Update(kills, now); i > 0; i--)
                AddStampBookmark(BookmarkType.Kill, result.Text);
            for (int i = _assists.Update(assists, now); i > 0; i--)
                AddStampBookmark(BookmarkType.Assist, result.Text);
        }

        // "KILL ASSIST" reads as KILL plus a second word that is often garbled ("A$ST"), or the other way round
        private static (int Kills, int Assists) CountStamps(OcrResult result)
        {
            var words = result.Lines
                .SelectMany(line => line.Words)
                .Select(word => new string(word.Text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
                .Where(word => word.Length > 0)
                .ToList();

            int kills = 0, assists = 0, loneAssists = 0;
            for (int i = 0; i < words.Count; i++)
            {
                if (IsKill(words[i]))
                {
                    if (i + 1 < words.Count && IsAssistTail(words[i + 1]))
                    {
                        assists++;
                        i++;
                    }
                    else
                        kills++;
                }
                else if (IsAssist(words[i]))
                    loneAssists++;
            }
            return (Math.Max(kills - loneAssists, 0), assists + loneAssists);
        }

        private static bool IsKill(string word) => word.Length >= 3 && LevenshteinDistance(word, "KILL") <= 1;
        private static bool IsAssist(string word) => word.Length >= 4 && LevenshteinDistance(word, "ASSIST") <= 2;
        private static bool IsAssistTail(string word) => !IsKill(word) && (IsAssist(word) || (word.Length >= 3 && word[0] == 'A'));

        private void AddStampBookmark(BookmarkType type, string text)
        {
            AddBookmark(type);
            Log.Information($"[Deadlock] Detected '{text}' stamp -> {type}");
        }

        // Stamps stay up for about 4.5 s and several can show at once
        private class StampCounter
        {
            private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);
            private readonly int[] _lastCounts = new int[3];
            private readonly List<DateTime> _counted = [];
            private int _polls;

            // Returns how many new stamps appeared, a stamp has to be read in 2 of the last 3 polls
            public int Update(int count, DateTime now)
            {
                _lastCounts[_polls++ % 3] = Math.Max(count, 0);
                int seen = _lastCounts.Order().ElementAt(1);

                _counted.RemoveAll(time => now - time >= Lifetime);
                int added = Math.Max(seen - _counted.Count, 0);
                for (int i = 0; i < added; i++)
                    _counted.Add(now);
                return added;
            }
        }
    }
}
