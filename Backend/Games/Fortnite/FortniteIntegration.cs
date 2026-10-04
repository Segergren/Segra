using Serilog;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.Fortnite
{
    /// <summary>
    /// Detects kills and deaths from the match replays Fortnite saves on PC, Fortnite has no log or API for them.
    ///
    /// With Record Replays on (the default), every match is saved to Saved\Demos. Its events hold every elimination in
    /// the match with the Epic account ids of the victim and the eliminator. They stay encrypted until the player leaves
    /// the match, so a replay is read once its header no longer says it is live. The player's own id comes from the
    /// login line in FortniteGame.log:
    /// <list type="bullet">
    /// <item>Kill: the player knocked someone, or eliminated someone who was not knocked. A knock that gets revived or
    /// finished by another player still counts, finishing someone else's knock does not.</item>
    /// <item>Death: the player got knocked, or was eliminated without being knocked first. Every knock counts, also
    /// those that get revived.</item>
    /// </list>
    ///
    /// Revives are not in the replay. An elimination within the bleed out time of the victim's last knock ends that
    /// knock and adds nothing, later ones count as new. Finishes in the replays come up to about 60 s after the knock.
    ///
    /// Event times are replay time from the header's local timestamp, taken when the replay started, and land within
    /// about half a second of the kill feed.
    ///
    /// Bookmarks are added when the player leaves the match. A warning is logged at start when Record Replays is off.
    /// </summary>
    internal partial class FortniteIntegration : Integration
    {
        private static readonly string SavedFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved");

        private static readonly TimeSpan BleedOutTime = TimeSpan.FromSeconds(65);

        private static ReadOnlySpan<byte> ReplaySettingTag => "\u0018\0\0\0bReplayRecordingEnabled\0\u000d\0\0\0BoolProperty\0"u8;

        private readonly string replayFolder = Path.Combine(SavedFolder, "Demos");
        private readonly string logFile = Path.Combine(SavedFolder, "Logs", "FortniteGame.log");
        private readonly System.Timers.Timer checkTimer = new(5000);
        private readonly HashSet<string> processedFiles = [];
        private DateTime startedAt;

        public FortniteIntegration()
        {
            checkTimer.Elapsed += (_, _) => TimerTick();
        }

        public override Task Start()
        {
            startedAt = DateTime.Now;
            Log.Information($"Initializing Fortnite replay integration. Watching {replayFolder}");
            WarnIfReplaysDisabled();
            checkTimer.Start();
            return Task.CompletedTask;
        }

        private static void WarnIfReplaysDisabled()
        {
            try
            {
                var cloud = new DirectoryInfo(Path.Combine(SavedFolder, "Cloud"));
                var settings = cloud.Exists
                    ? cloud.GetFiles("ClientSettings.Sav", SearchOption.AllDirectories).MaxBy(f => f.LastWriteTime)
                    : null;
                if (settings == null)
                    return;

                // 16 byte header, then zlib compressed properties (only non-default values are saved)
                using var file = settings.OpenRead();
                file.Position = 16;
                using var zlib = new ZLibStream(file, CompressionMode.Decompress);
                using var data = new MemoryStream();
                zlib.CopyTo(data);

                var bytes = data.GetBuffer().AsSpan(0, (int)data.Length);
                var index = bytes.IndexOf(ReplaySettingTag);
                // Bool value (old tag layout) or tag flags (UE5, 0x10 = true), 0 means false in both
                var value = index + ReplaySettingTag.Length + 8;
                if (index >= 0 && value < bytes.Length && bytes[value] == 0)
                    Log.Warning("Record Replays is turned off in Fortnite, kills and deaths will not be bookmarked");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to check the Fortnite replay setting: {ex.Message}");
            }
        }

        public override Task Shutdown()
        {
            Log.Information("Stopping Fortnite replay integration.");
            checkTimer.Stop();
            // Leaving the match right before closing the game finalizes the replay as the recording stops
            TimerTick(final: true);
            return Task.CompletedTask;
        }

        private void TimerTick(bool final = false)
        {
            lock (processedFiles)
            {
                try
                {
                    if (!Directory.Exists(replayFolder))
                        return;

                    foreach (var file in Directory.EnumerateFiles(replayFolder, "*.replay"))
                    {
                        if (processedFiles.Contains(file))
                            continue;

                        var info = new FileInfo(file);
                        if (info.LastWriteTime < startedAt)
                        {
                            processedFiles.Add(file);
                            continue;
                        }

                        if (!final && DateTime.Now - info.LastWriteTime < TimeSpan.FromSeconds(3))
                            continue;

                        Replay replay;
                        try
                        {
                            replay = ReplayReader.Read(file);
                        }
                        catch (Exception ex)
                        {
                            processedFiles.Add(file);
                            Log.Warning($"Failed to parse Fortnite replay {file}: {ex.Message}");
                            continue;
                        }

                        // The events stay encrypted until the match is over
                        if (replay.IsLive)
                            continue;

                        processedFiles.Add(file);
                        ProcessMatch(file, replay);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Fortnite integration encountered an error: {ex.Message}");
                }
            }
        }

        private void ProcessMatch(string file, Replay replay)
        {
            Log.Information($"New Fortnite replay: {file}");

            var me = ReadAccountId();
            if (me == null)
            {
                Log.Warning("Fortnite account id not found in FortniteGame.log, skipping replay");
                return;
            }

            var recording = AppState.Instance.Recording;
            if (recording == null)
                return;

            var lastKnocks = new Dictionary<string, TimeSpan>();
            var added = 0;

            foreach (var elim in replay.Eliminations)
            {
                if (elim.Knocked)
                    lastKnocks[elim.Eliminated] = elim.Time;
                else if (lastKnocks.Remove(elim.Eliminated, out var knockTime) && elim.Time - knockTime < BleedOutTime)
                    continue;

                if (elim.Eliminated == me)
                    added += AddBookmark(recording, BookmarkType.Death, replay.Timestamp + elim.Time);
                else if (elim.Eliminator == me)
                    added += AddBookmark(recording, BookmarkType.Kill, replay.Timestamp + elim.Time);
            }

            Log.Information($"Fortnite match with {replay.Eliminations.Count} eliminations: added {added} bookmarks");
        }

        private string? ReadAccountId()
        {
            try
            {
                using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    var match = AccountIdRegex().Match(line);
                    if (match.Success)
                        return match.Groups[1].Value.ToLowerInvariant();
                }
            }
            catch (IOException ex)
            {
                Log.Warning($"Failed to read {logFile}: {ex.Message}");
            }

            return null;
        }

        private static int AddBookmark(Recording recording, BookmarkType type, DateTime time)
        {
            var offset = time - recording.StartTime;
            if (offset < TimeSpan.Zero)
                return 0;

            recording.AddBookmark(new Bookmark
            {
                Type = type,
                Time = offset
            });
            return 1;
        }

        [GeneratedRegex(@"Successfully logged in user\. UserId=\[([0-9a-fA-F]{32})\]")]
        private static partial Regex AccountIdRegex();
    }
}
