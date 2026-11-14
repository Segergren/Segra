using Serilog;
using System.ComponentModel;
using System.Diagnostics;
using Segra.Backend.Media;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games
{
    /// <summary>
    /// Base class for games that send their events to the Steam timeline. Subclasses map the game's markers to
    /// bookmark types, see <see cref="SteamTimelineReader"/> for how the markers are read.
    ///
    /// Steam only hands the timeline over when the game closes, so the bookmarks are added at that point: to the
    /// recording when it is still running, otherwise to the finished session. A timeline covers the run of the game
    /// and is shared by every recording made during it.
    /// </summary>
    internal abstract class SteamTimelineIntegration : Integration
    {
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

        // Running games by process id
        private static readonly Dictionary<int, GameRun> _runs = [];

        protected abstract string LogPrefix { get; }

        /// <summary>
        /// The bookmark type for a timeline event, null for events that get no bookmark.
        /// </summary>
        protected abstract BookmarkType? GetBookmarkType(SteamTimelineEvent timelineEvent);

        public override Task Start()
        {
            var recording = AppState.Instance.Recording;
            var appId = string.IsNullOrEmpty(ExePath) ? null : SteamUtils.GetAppInfoFromExePath(ExePath)?.AppId;
            if (recording?.Pid is not int pid || appId is not int steamAppId)
            {
                Log.Information($"[{LogPrefix}] No Steam app or game process found, Steam timeline events will not be imported");
                return Task.CompletedTask;
            }

            lock (_runs)
            {
                if (!_runs.TryGetValue(pid, out var run))
                {
                    run = new GameRun(pid, steamAppId, LogPrefix);
                    _runs[pid] = run;
                    _ = Task.Run(run.Watch);
                }
                run.Recordings.Add((recording, this));
            }

            Log.Information($"[{LogPrefix}] Waiting for the Steam timeline of app {steamAppId}, read when the game closes");
            return Task.CompletedTask;
        }

        // The game run keeps going, the timeline arrives after the recording has stopped
        public override Task Shutdown() => Task.CompletedTask;

        private sealed class GameRun(int pid, int appId, string logPrefix)
        {
            // Guarded by the lock on _runs
            public List<(Recording Recording, SteamTimelineIntegration Integration)> Recordings { get; } = [];

            private readonly Dictionary<string, SteamTimelineFile> _kept = [];

            // Timelines from before the game started belong to an earlier run that Steam still has recordings of
            private DateTime? _since = StartTime(pid);

            public async Task Watch()
            {
                try
                {
                    SteamTimelineReader.RemoveLeftovers();

                    // Steam lists a timeline a few seconds after the game starts it, and starts a new one after sleep
                    for (int tick = 0; IsRunning(); tick++)
                    {
                        if (tick % 50 == 0)
                            KeepTimelines();
                        await Task.Delay(100);
                    }

                    KeepTimelines();
                    if (_kept.Count == 0)
                    {
                        Log.Information($"[{logPrefix}] Steam had no timeline for the game, nothing to import");
                        return;
                    }

                    // Steam writes the last timeline a few seconds after the game closes
                    var closed = Stopwatch.StartNew();
                    var pending = _kept.Values.OrderBy(t => t.Started).ToList();
                    var newest = pending[^1];
                    var timelines = new List<SteamTimeline>();
                    while (pending.Contains(newest) && closed.Elapsed < WriteTimeout)
                    {
                        await Task.Delay(100);
                        Read(pending, timelines, closed.Elapsed);
                    }

                    if (pending.Count > 0 && !pending.Contains(newest))
                    {
                        await Task.Delay(1000);
                        Read(pending, timelines, closed.Elapsed);
                    }

                    foreach (var missing in pending)
                        Log.Warning($"[{logPrefix}] Steam did not write {missing.Id} within {WriteTimeout.TotalSeconds:0} s, its events are not imported");
                    if (timelines.Count == 0)
                        return;

                    List<(Recording Recording, SteamTimelineIntegration Integration)> recordings;
                    lock (_runs)
                    {
                        _runs.Remove(pid);
                        recordings = [.. Recordings];
                    }

                    foreach (var (recording, integration) in recordings)
                        await Import(timelines, recording, integration);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[{logPrefix}] Failed to import the Steam timeline: {ex.Message}");
                }
                finally
                {
                    foreach (var timeline in _kept.Values)
                        SteamTimelineReader.Release(timeline);

                    lock (_runs)
                    {
                        if (_runs.TryGetValue(pid, out var run) && run == this)
                            _runs.Remove(pid);
                    }
                }
            }

            private void KeepTimelines()
            {
                var found = SteamTimelineReader.FindTimelines(appId);
                if (found.Count == 0)
                    return;

                _since ??= found[^1].Started;
                foreach (var timeline in found)
                {
                    if (timeline.Started < _since || !_kept.TryAdd(timeline.Id, timeline))
                        continue;

                    if (SteamTimelineReader.Keep(timeline))
                        Log.Information($"[{logPrefix}] Keeping Steam timeline {timeline.Id}");
                    else
                        Log.Warning($"[{logPrefix}] Could not link Steam timeline {timeline.Id}, it is only read if Steam leaves the file");
                }
            }

            private void Read(List<SteamTimelineFile> pending, List<SteamTimeline> timelines, TimeSpan sinceClosed)
            {
                foreach (var file in pending.ToList())
                {
                    if (SteamTimelineReader.Read(file) is not SteamTimeline timeline)
                        continue;

                    pending.Remove(file);
                    timelines.Add(timeline);

                    var kinds = timeline.Events.GroupBy(e => $"{e.Title} ({e.Icon})").Select(g => $"{g.Count()}x {g.Key}");
                    Log.Information($"[{logPrefix}] Steam timeline {file.Id} with {timeline.Events.Count} events, read {sinceClosed.TotalSeconds:0.0} s after the game closed: {string.Join(", ", kinds)}");
                    foreach (var timelineEvent in timeline.Events)
                        Log.Debug($"[{logPrefix}] {timeline.Start + timelineEvent.Time:HH:mm:ss.fff} {timelineEvent.Title} | {timelineEvent.Description} | {timelineEvent.Icon}");
                }
            }

            private static DateTime? StartTime(int pid)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    return process.StartTime - TimeSpan.FromSeconds(5);
                }
                catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException)
                {
                    // Anti-cheat can deny access to the process, the newest timeline is then taken as its first
                    return null;
                }
            }

            private bool IsRunning()
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    return !process.HasExited;
                }
                catch (ArgumentException)
                {
                    return false;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Anti-cheat can deny access to the process, it is still there
                    return true;
                }
            }

            private async Task Import(List<SteamTimeline> timelines, Recording recording, SteamTimelineIntegration integration)
            {
                var bookmarks = new List<Bookmark>();
                foreach (var timeline in timelines)
                {
                    foreach (var timelineEvent in timeline.Events)
                    {
                        if (integration.GetBookmarkType(timelineEvent) is not BookmarkType type)
                            continue;

                        var time = timeline.Start + timelineEvent.Time;
                        if (time < recording.StartTime || time > recording.EndTime)
                            continue;

                        bookmarks.Add(new Bookmark { Type = type, Time = time - recording.StartTime });
                    }
                }

                // A stopped recording is written to the session from its bookmark list, it is left alone until that is done
                if (!IsRecording(recording))
                    await WaitUntilFinished(recording);

                var session = IsRecording(recording) ? null : FindSession(recording);

                // Also reaches the replays saved during the recording
                foreach (var bookmark in bookmarks)
                    recording.AddBookmark(bookmark);

                if (session == null && IsRecording(recording))
                {
                    // The game closing usually stops the recording, which may have read the bookmarks just before these
                    for (int i = 0; i < 20 && IsRecording(recording); i++)
                        await Task.Delay(500);
                    if (!IsRecording(recording))
                    {
                        await WaitUntilFinished(recording);
                        session = FindSession(recording);
                    }
                }

                if (session != null)
                {
                    // A session saved without these bookmarks had nothing to make its highlight from
                    bool highlight = Settings.Instance.EnableAi && Settings.Instance.AutoGenerateHighlights &&
                        session.Bookmarks?.Any(b => b.Type.IncludeInHighlight()) != true &&
                        bookmarks.Any(b => b.Type.IncludeInHighlight());

                    await ContentService.AddBookmarks(session.Id, bookmarks);
                    if (highlight)
                        _ = AiService.CreateHighlight(session.Id);
                }

                var file = Path.GetFileName(recording.FilePath) ?? "the replay buffer";
                if (session != null || IsRecording(recording))
                    Log.Information($"[{logPrefix}] Added {bookmarks.Count} bookmarks from the Steam timeline to {file}");
                else
                    Log.Information($"[{logPrefix}] {file} was not kept as a session, its {bookmarks.Count} bookmarks from the Steam timeline only went to replays saved from it");
            }

            private static bool IsRecording(Recording recording) => ReferenceEquals(AppState.Instance.Recording, recording);

            private static async Task WaitUntilFinished(Recording recording)
            {
                for (int i = 0; i < 600; i++)
                {
                    // Without a session when the recording was discarded or only filled the replay buffer
                    if (FindSession(recording) != null || !AppState.Instance.IsFinishingRecording)
                        return;
                    await Task.Delay(500);
                }
            }

            private static Content? FindSession(Recording recording)
            {
                if (recording.FilePath == null)
                    return null;

                try
                {
                    return AppState.Instance.Content.FirstOrDefault(c =>
                        c.Type == Content.ContentType.Session &&
                        string.Equals(c.FilePath, recording.FilePath, StringComparison.OrdinalIgnoreCase));
                }
                catch (InvalidOperationException)
                {
                    // The content list changed while searching
                    return null;
                }
            }
        }
    }
}
