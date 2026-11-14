using Serilog;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games
{
    // Records the matches an integration sees on the recording in progress
    internal sealed class MatchTracker
    {
        private readonly object _lock = new();
        private Recording? _recording;
        private GameMatch? _match;
        private bool _ended;

        public bool IsRunning
        {
            get { lock (_lock) return IsCurrent() && !_ended; }
        }

        // startedAgo backdates the start for matches first seen after they began
        public void Start(Action<GameMatch>? init = null, TimeSpan startedAgo = default)
        {
            lock (_lock)
            {
                EndRunning(null);
                _recording = null;
                _match = null;

                var recording = AppState.Instance.Recording;
                if (recording == null)
                    return;

                TimeSpan now = DateTime.Now - recording.StartTime;
                TimeSpan start = now - startedAgo < TimeSpan.Zero ? TimeSpan.Zero : now - startedAgo;
                var match = new GameMatch { StartTime = start, EndTime = now };
                init?.Invoke(match);
                recording.AddMatch(match);
                _recording = recording;
                _match = match;
                _ended = false;
                Log.Information($"Match started at {start}");
            }
        }

        // While the match runs its end follows the latest update, so a match that never ends properly keeps what was seen
        public void Update(Action<GameMatch> update)
        {
            lock (_lock)
            {
                if (!IsCurrent())
                    return;

                TimeSpan now = DateTime.Now - _recording!.StartTime;
                bool running = !_ended;
                _recording.UpdateMatch(_match!, m =>
                {
                    if (running)
                        m.EndTime = now;
                    update(m);
                });
            }
        }

        public void End(Action<GameMatch>? update = null)
        {
            lock (_lock)
            {
                EndRunning(update);
            }
        }

        // Ends the match where it was last seen, for games that only learn it ended once the next one begins
        public void Close()
        {
            lock (_lock)
            {
                if (IsCurrent() && !_ended)
                {
                    _ended = true;
                    Log.Information($"Match closed at {_match!.EndTime} ({_match.Result?.ToString() ?? "no result"})");
                }
            }
        }

        // Continues the last match, e.g. after reconnecting to the same game
        public bool Resume()
        {
            lock (_lock)
            {
                if (!IsCurrent() || !_ended)
                    return false;

                _ended = false;
                Log.Information("Match resumed");
                return true;
            }
        }

        private void EndRunning(Action<GameMatch>? update)
        {
            if (!IsCurrent() || _ended)
                return;

            TimeSpan now = DateTime.Now - _recording!.StartTime;
            _recording.UpdateMatch(_match!, m =>
            {
                m.EndTime = now;
                update?.Invoke(m);
            });
            _ended = true;
            Log.Information($"Match ended at {now} ({_match!.Result?.ToString() ?? "no result"})");
        }

        private bool IsCurrent() => _match != null && _recording != null && ReferenceEquals(_recording, AppState.Instance.Recording);
    }
}
