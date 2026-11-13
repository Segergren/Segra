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
    /// recording when it is still running, otherwise to the finished session. One timeline covers the whole run of
    /// the game and is shared by every recording made during it.
    /// </summary>
    internal abstract class SteamTimelineIntegration : Integration
    {
        private static readonly TimeSpan CatchTimeout = TimeSpan.FromSeconds(30);

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

            public async Task Watch()
            {
                try
                {
                    // Steam lists the timeline a few seconds after the game starts it
                    string? file = null;
                    for (int tick = 0; IsRunning(); tick++)
                    {
                        if (file == null && tick % 50 == 0)
                            file = SteamTimelineReader.FindTimelineFile(appId);
                        await Task.Delay(100);
                    }

                    file ??= SteamTimelineReader.FindTimelineFile(appId);
                    if (file == null)
                    {
                        Log.Information($"[{logPrefix}] Steam had no timeline for the game, nothing to import");
                        return;
                    }

                    var stopwatch = Stopwatch.StartNew();
                    var timeline = await Task.Factory.StartNew(
                        () => SteamTimelineReader.Catch(file, CatchTimeout), TaskCreationOptions.LongRunning);
                    if (timeline == null)
                    {
                        Log.Warning($"[{logPrefix}] Steam did not write {Path.GetFileName(file)} within {CatchTimeout.TotalSeconds:0} s, no timeline events imported");
                        return;
                    }

                    var kinds = timeline.Events.GroupBy(e => $"{e.Title} ({e.Icon})").Select(g => $"{g.Count()}x {g.Key}");
                    Log.Information($"[{logPrefix}] Steam timeline with {timeline.Events.Count} events, written {stopwatch.Elapsed.TotalSeconds:0.0} s after the game closed: {string.Join(", ", kinds)}");
                    foreach (var timelineEvent in timeline.Events)
                        Log.Debug($"[{logPrefix}] {timeline.Start + timelineEvent.Time:HH:mm:ss.fff} {timelineEvent.Title} | {timelineEvent.Description} | {timelineEvent.Icon}");

                    List<(Recording Recording, SteamTimelineIntegration Integration)> recordings;
                    lock (_runs)
                    {
                        _runs.Remove(pid);
                        recordings = [.. Recordings];
                    }

                    foreach (var (recording, integration) in recordings)
                        await Import(timeline, recording, integration);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[{logPrefix}] Failed to import the Steam timeline: {ex.Message}");
                }
                finally
                {
                    lock (_runs)
                    {
                        if (_runs.TryGetValue(pid, out var run) && run == this)
                            _runs.Remove(pid);
                    }
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

            private async Task Import(SteamTimeline timeline, Recording recording, SteamTimelineIntegration integration)
            {
                var bookmarks = new List<Bookmark>();
                foreach (var timelineEvent in timeline.Events)
                {
                    if (integration.GetBookmarkType(timelineEvent) is not BookmarkType type)
                        continue;

                    var time = timeline.Start + timelineEvent.Time;
                    if (time < recording.StartTime || time > recording.EndTime)
                        continue;

                    bookmarks.Add(new Bookmark { Type = type, Time = time - recording.StartTime });
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
                    await ContentService.AddBookmarks(session.Id, bookmarks);

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
