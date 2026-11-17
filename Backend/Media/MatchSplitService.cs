using Serilog;
using Segra.Backend.App;
using System.Globalization;
using System.Runtime.CompilerServices;
using Segra.Backend.Core;
using Segra.Backend.Core.Models;
using Segra.Backend.Games;
using Segra.Backend.Shared;
using Segra.Backend.Recorder;
using Segra.Backend.Windows.Storage;
using static Segra.Backend.Shared.GeneralUtils;

namespace Segra.Backend.Media
{
    // Turns a finished session recording into one session per match
    internal static class MatchSplitService
    {
        private static readonly TimeSpan PaddingBefore = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PaddingAfter = TimeSpan.FromSeconds(15);
        // Copying a single match isn't worth it for a few minutes of menus
        private static readonly TimeSpan MinTrimForSingleMatch = TimeSpan.FromMinutes(3);
        // Cuts are written under this suffix and renamed once all of them are complete
        private const string PartialSuffix = ".partial";

        private sealed record Part(GameMatch Match, TimeSpan Start, TimeSpan End)
        {
            public string? FilePath { get; set; }
            public WebcamLayer? Webcam { get; set; }
            // Where the cut file really starts, since a stream copy starts at the keyframe before Start
            public TimeSpan Origin { get; set; }

            public bool Contains(TimeSpan time) => time >= Start && time <= End;
        }

        public sealed record SplitSession(string Id, List<Bookmark> Bookmarks);

        /// <summary>
        /// Saves each match as its own session and deletes the recording. Returns the new sessions, or null when
        /// the recording should be saved whole instead. keepOnlyBookmarked drops matches without a manual bookmark.
        /// </summary>
        public static async Task<List<SplitSession>?> SplitAsync(Recording session, string filePath, WebcamLayer? webcam, int? igdbId, bool keepOnlyBookmarked)
        {
            if (session.GetFiles().Count > 1)
                return await SaveSplitFilesAsync(session, igdbId, keepOnlyBookmarked, includeCurrent: true);

            return await SplitFileAsync(session, filePath, TimeSpan.Zero, session.GetMatches(), session.GetBookmarks(), webcam, igdbId, keepOnlyBookmarked);
        }

        // How far the files of a split recording have been saved
        private sealed class SplitProgress
        {
            public readonly SemaphoreSlim Lock = new(1, 1);
            public int SavedFiles;
            // Where the next file starts in the recording, measured from the saved files
            public TimeSpan NextStart;
        }

        private static readonly ConditionalWeakTable<Recording, SplitProgress> _progress = new();

        /// <summary>
        /// Saves the file the recording just finished writing after a split, while it keeps recording.
        /// </summary>
        public static void SaveFinishedFile(Recording session, bool keepOnlyBookmarked)
        {
            var work = BackgroundWork.Begin();
            _ = Task.Run(async () =>
            {
                using (work)
                {
                    try
                    {
                        int? igdbId = !string.IsNullOrEmpty(session.ExePath) ? GameUtils.GetIgdbIdFromExePath(session.ExePath) : null;
                        var saved = await SaveSplitFilesAsync(session, igdbId, keepOnlyBookmarked, includeCurrent: false);
                        if (saved.Count == 0)
                            return;
                        await SettingsService.LoadContentFromFolderIntoState(false);
                        CreateHighlights(saved);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to save the finished recording file");
                    }
                }
            });
        }

        public static void CreateHighlights(IEnumerable<SplitSession> sessions)
        {
            if (!Settings.Instance.EnableAi || !Settings.Instance.AutoGenerateHighlights)
                return;
            foreach (var saved in sessions.Where(s => s.Bookmarks.Any(b => b.Type.IncludeInHighlight())))
                _ = AiService.CreateHighlight(saved.Id);
        }

        /// <summary>
        /// Deletes the files of a discarded recording that weren't saved while it was recording.
        /// </summary>
        public static async Task DiscardUnsavedFilesAsync(Recording session)
        {
            var progress = _progress.GetOrCreateValue(session);
            await progress.Lock.WaitAsync();
            try
            {
                var files = session.GetFiles();
                for (int i = progress.SavedFiles; i < files.Count; i++)
                    DeleteOriginal(files[i].Path, i == 0 ? session.Webcam?.FilePath : null);
                progress.SavedFiles = files.Count;
            }
            finally
            {
                progress.Lock.Release();
            }
        }

        // For a recording that is split at match ends: each file becomes a session, and only a file that still holds
        // several matches or long menus before its match is cut further. The file being written is left for later
        // unless includeCurrent is set.
        private static async Task<List<SplitSession>> SaveSplitFilesAsync(Recording session, int? igdbId, bool keepOnlyBookmarked, bool includeCurrent)
        {
            var progress = _progress.GetOrCreateValue(session);
            await progress.Lock.WaitAsync();
            try
            {
                var files = session.GetFiles();
                int count = includeCurrent ? files.Count : files.Count - 1;
                var saved = new List<SplitSession>();
                for (; progress.SavedFiles < count; progress.SavedFiles++)
                {
                    int i = progress.SavedFiles;
                    // Where the next file starts by the clock, for when this one can't be measured
                    TimeSpan? nextStart = i + 1 < files.Count ? files[i + 1].Start : null;
                    progress.NextStart = await SaveSplitFileAsync(session, files[i].Path, progress.NextStart, nextStart, igdbId, keepOnlyBookmarked, saved);
                }
                return saved;
            }
            finally
            {
                progress.Lock.Release();
            }
        }

        // Saves one file of a split recording into saved and returns where the next file starts
        private static async Task<TimeSpan> SaveSplitFileAsync(Recording session, string file, TimeSpan fileStart, TimeSpan? nextStart, int? igdbId, bool keepOnlyBookmarked, List<SplitSession> saved)
        {
            Log.Information($"Saving {file}");
            List<GameMatch> allMatches = session.GetMatches();
            List<Bookmark> allBookmarks = session.GetBookmarks();
            bool splitIntoMatches = session.SplitIntoMatches;
            if (!File.Exists(file))
            {
                Log.Warning($"Split file {file} is missing");
                return nextStart ?? fileStart;
            }
            await EnsureFileReady(file);
            TimeSpan duration = await FFmpegService.GetVideoDuration(file);
            if (duration <= TimeSpan.Zero)
            {
                Log.Warning($"Could not read the length of {file}, saving it without matches");
                if (await SaveWholeAsync(session, file, fileStart, nextStart ?? fileStart, [], [], igdbId) is SplitSession unplaced)
                    saved.Add(unplaced);
                return nextStart ?? fileStart;
            }

            TimeSpan offset = fileStart;
            TimeSpan fileEnd = fileStart + duration;
            List<GameMatch> matches = allMatches
                .Where(m => m.EndTime > offset && m.StartTime < fileEnd)
                .Select(m =>
                {
                    var shifted = m.Shifted(-offset);
                    shifted.StartTime = Max(TimeSpan.Zero, shifted.StartTime);
                    shifted.EndTime = Min(duration, shifted.EndTime);
                    return shifted;
                })
                .ToList();
            List<Bookmark> bookmarks = allBookmarks
                .Where(b => b.Time >= offset && b.Time < fileEnd)
                .Select(b => new Bookmark { Type = b.Type, Subtype = b.Subtype, Time = b.Time - offset })
                .ToList();
            bool hasManualBookmark = bookmarks.Any(b => b.Type == BookmarkType.Manual);

            // Menus between matches, or a file without the manual bookmark it needs to be kept
            if ((splitIntoMatches && matches.Count == 0 && !hasManualBookmark) || (keepOnlyBookmarked && !hasManualBookmark))
            {
                Log.Information($"Deleting {file}: {(matches.Count == 0 ? "no match in it" : "no manual bookmark")}");
                DeleteOriginal(file, null);
                return fileEnd;
            }

            if (splitIntoMatches && matches.Count > 0
                && await SplitFileAsync(session, file, offset, matches, bookmarks, null, igdbId, keepOnlyBookmarked) is { } parts)
            {
                saved.AddRange(parts);
                return fileEnd;
            }

            if (await SaveWholeAsync(session, file, offset, fileEnd, bookmarks, matches, igdbId) is SplitSession whole)
                saved.Add(whole);
            return fileEnd;
        }

        private static async Task<SplitSession?> SaveWholeAsync(Recording session, string file, TimeSpan offset, TimeSpan end, List<Bookmark> bookmarks, List<GameMatch> matches, int? igdbId)
        {
            string? id = await ContentService.CreateMetadataFile(file, Content.ContentType.Session, session.Game, bookmarks,
                createdAt: session.StartTime + end, igdbId: igdbId, audioTrackNames: session.AudioTrackNames, audioTrackTypes: session.AudioTrackTypes,
                gameExePath: session.ExePath, matches: matches.Count > 0 ? matches : null);
            if (id == null)
                return null;

            await ContentService.CreateThumbnail(file, Content.ContentType.Session, id);
            var waveformWork = BackgroundWork.Begin();
            _ = Task.Run(async () =>
            {
                using (waveformWork)
                    await ContentService.CreateWaveformFile(file, Content.ContentType.Session, id);
            });
            return new SplitSession(id, bookmarks);
        }

        // Matches and bookmarks are relative to the file, which starts at fileStart in the recording
        private static async Task<List<SplitSession>?> SplitFileAsync(Recording session, string filePath, TimeSpan fileStart, List<GameMatch> matches, List<Bookmark> bookmarks, WebcamLayer? webcam, int? igdbId, bool keepOnlyBookmarked)
        {
            if (matches.Count == 0)
                return null;

            TimeSpan duration = await FFmpegService.GetVideoDuration(filePath);
            if (duration <= TimeSpan.Zero)
            {
                Log.Warning($"Could not read the length of {filePath}, keeping the session whole");
                return null;
            }

            var parts = matches
                .OrderBy(m => m.StartTime)
                .Select(m => new Part(m, Max(TimeSpan.Zero, m.StartTime - PaddingBefore), Min(duration, m.EndTime + PaddingAfter)))
                .Where(p => p.End > p.Start)
                .ToList();
            if (parts.Count == 0)
                return null;

            var manualBookmarks = bookmarks.Where(b => b.Type == BookmarkType.Manual).ToList();
            if (manualBookmarks.Any(b => !parts.Any(p => p.Contains(b.Time))))
            {
                Log.Information("Keeping the session whole: a manual bookmark is outside every match");
                return null;
            }

            if (keepOnlyBookmarked)
            {
                parts = parts.Where(p => manualBookmarks.Any(b => p.Contains(b.Time))).ToList();
                if (parts.Count == 0)
                    return null;
            }

            // A single match is trimmed in place, which takes no time; the unused start is freed later.
            // A file open in the player can't be changed under it, so that one is cut into a new file.
            if (parts.Count == 1 && webcam == null && !MessageService.IsViewing(filePath)
                && await TrimInPlaceAsync(session, filePath, fileStart, parts[0], bookmarks, igdbId) is { } trimmed)
                return trimmed;

            TimeSpan kept = TimeSpan.FromSeconds(parts.Sum(p => (p.End - p.Start).TotalSeconds));
            if (matches.Count == 1 && duration - kept < MinTrimForSingleMatch)
            {
                Log.Information($"Keeping the session whole: splitting would only trim {(int)(duration - kept).TotalSeconds} s");
                return null;
            }

            double keptFraction = kept.TotalSeconds / duration.TotalSeconds;
            long sourceBytes = new FileInfo(filePath).Length + (webcam != null && File.Exists(webcam.FilePath) ? new FileInfo(webcam.FilePath).Length : 0);
            long neededBytes = (long)(sourceBytes * keptFraction) + 100L * 1024 * 1024;
            if (StorageService.GetContentDriveFreeBytes() is long freeBytes && freeBytes < neededBytes)
            {
                Log.Warning($"Not enough free space to split the session into matches ({neededBytes / 1e9:F1} GB needed), keeping it whole");
                return null;
            }

            Log.Information($"Splitting {filePath} into {parts.Count} match session(s)");
            string folder = Path.GetDirectoryName(filePath)!;
            DeleteLeftoverPartials(folder);
            if (webcam != null)
                DeleteLeftoverPartials(Path.GetDirectoryName(webcam.FilePath));

            try
            {
                foreach (var part in parts)
                {
                    DateTime matchStart = session.StartTime + fileStart + part.Match.StartTime;
                    part.FilePath = GetUniqueFilePath(PathUtils.Combine(folder, $"{matchStart:yyyy-MM-dd_HH-mm-ss}.mp4"));
                    string partial = part.FilePath + PartialSuffix;
                    TimeSpan length = part.End - part.Start;
                    await CutAsync(filePath, partial, part.Start, length, session.AudioTrackNames);
                    await EnsureFileReady(partial);

                    TimeSpan cutDuration = await FFmpegService.GetVideoDuration(partial);
                    if (cutDuration <= TimeSpan.Zero)
                        throw new Exception($"Could not read the length of {partial}");
                    TimeSpan preroll = Min(TimeSpan.FromSeconds(2), Max(TimeSpan.Zero, cutDuration - length));
                    part.Origin = part.Start - preroll;

                    if (webcam != null)
                        part.Webcam = await CutWebcamAsync(webcam, part, cutDuration);
                }

                foreach (var part in parts)
                {
                    File.Move(part.FilePath! + PartialSuffix, part.FilePath!);
                    if (part.Webcam != null)
                        File.Move(part.Webcam.FilePath + PartialSuffix, part.Webcam.FilePath);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to split the session into matches, keeping it whole");
                foreach (var part in parts)
                {
                    if (part.FilePath != null)
                    {
                        TryDelete(part.FilePath + PartialSuffix);
                        TryDelete(part.FilePath);
                    }
                    if (part.Webcam != null)
                    {
                        TryDelete(part.Webcam.FilePath + PartialSuffix);
                        ContentService.DeleteWebcamFile(part.Webcam.FilePath);
                    }
                }
                return null;
            }

            var created = new List<SplitSession>();
            foreach (var part in parts)
            {
                var partBookmarks = bookmarks
                    .Where(b => part.Contains(b.Time))
                    .Select(b => new Bookmark { Type = b.Type, Subtype = b.Subtype, Time = Max(TimeSpan.Zero, b.Time - part.Origin) })
                    .ToList();
                GameMatch partMatch = part.Match.Shifted(-part.Origin);

                string? id = await ContentService.CreateMetadataFile(part.FilePath!, Content.ContentType.Session, session.Game, partBookmarks,
                    createdAt: session.StartTime + fileStart + part.End, igdbId: igdbId, audioTrackNames: session.AudioTrackNames, audioTrackTypes: session.AudioTrackTypes,
                    gameExePath: session.ExePath, webcam: part.Webcam, matches: [partMatch]);
                if (id == null)
                {
                    ContentService.DeleteWebcamFile(part.Webcam?.FilePath);
                    continue;
                }

                await ContentService.CreateThumbnail(part.FilePath!, Content.ContentType.Session, id);
                var waveformWork = BackgroundWork.Begin();
                string partPath = part.FilePath!;
                _ = Task.Run(async () =>
                {
                    using (waveformWork)
                        await ContentService.CreateWaveformFile(partPath, Content.ContentType.Session, id);
                });
                created.Add(new SplitSession(id, partBookmarks));
            }

            if (created.Count < parts.Count)
            {
                Log.Warning("Some match sessions could not be saved, keeping the whole session too");
                return null;
            }

            DeleteOriginal(filePath, webcam?.FilePath);
            Log.Information($"Split the session into {created.Count} match session(s)");
            return created;
        }

        // Unused bytes worth a rewrite of the whole file to free
        private const long MinReclaimBytes = 256L * 1024 * 1024;

        // Returns null when the file couldn't be trimmed and is unchanged
        private static async Task<List<SplitSession>?> TrimInPlaceAsync(Recording session, string filePath, TimeSpan fileStart, Part part, List<Bookmark> bookmarks, int? igdbId)
        {
            var trim = await EmbeddedMetadataService.TrimIndexAsync(filePath, part.Start, part.End);
            if (trim == null)
                return null;

            part.Origin = trim.Start;
            var partBookmarks = bookmarks
                .Where(b => part.Contains(b.Time))
                .Select(b => new Bookmark { Type = b.Type, Subtype = b.Subtype, Time = Max(TimeSpan.Zero, b.Time - part.Origin) })
                .ToList();
            var saved = await SaveWholeAsync(session, filePath, fileStart, fileStart + part.End, partBookmarks, [part.Match.Shifted(-part.Origin)], igdbId);
            if (saved == null)
                return [];

            if (trim.UnusedBytes >= MinReclaimBytes)
                SpaceReclaimService.Enqueue(filePath);
            return [saved];
        }

        /// <summary>
        /// Ends the recording file once the padding after a match is recorded, so saving the match needs no copying.
        /// </summary>
        public static void SplitAfterMatch(Func<bool> matchRunning)
        {
            var recording = AppState.Instance.Recording;
            if (recording == null)
                return;

            _ = Task.Run(async () =>
            {
                await Task.Delay(PaddingAfter);
                // A resumed or new match would be cut in two
                if (matchRunning())
                    return;
                if (ReferenceEquals(AppState.Instance.Recording, recording) && OBSService.SplitRecordingFile())
                    Log.Information("Splitting the recording after the match");
            });
        }

        private static readonly object _originalLock = new();
        private static (string FilePath, string? WebcamPath)? _pendingOriginal;

        // A recording open in the player stays until the player leaves it
        public static void DeleteOriginal(string filePath, string? webcamPath)
        {
            lock (_originalLock)
            {
                if (MessageService.IsViewing(filePath))
                {
                    Log.Information($"Keeping {filePath} until it is closed in the player");
                    _pendingOriginal = (filePath, webcamPath);
                    return;
                }
            }
            TryDelete(filePath);
            ContentService.DeleteWebcamFile(webcamPath);
        }

        public static void DeletePendingOriginal()
        {
            (string FilePath, string? WebcamPath)? pending;
            lock (_originalLock)
            {
                pending = _pendingOriginal;
                if (pending == null || MessageService.IsViewing(pending.Value.FilePath))
                    return;
                _pendingOriginal = null;
            }
            Log.Information($"Deleting {pending.Value.FilePath} after it was closed in the player");
            TryDelete(pending.Value.FilePath);
            ContentService.DeleteWebcamFile(pending.Value.WebcamPath);
        }

        // Cuts the same span from the webcam file. Both cuts end at the same moment, so the difference
        // in length is where the webcam starts in the match video (as in OBSService.FinishWebcamLayer).
        private static async Task<WebcamLayer?> CutWebcamAsync(WebcamLayer webcam, Part part, TimeSpan cutDuration)
        {
            TimeSpan offset = TimeSpan.FromSeconds(webcam.Offset);
            if (part.End <= offset || !File.Exists(webcam.FilePath))
                return null;

            TimeSpan start = Max(TimeSpan.Zero, part.Start - offset);
            TimeSpan length = part.End - offset - start;
            string webcamPath = GetUniqueFilePath(PathUtils.Combine(Path.GetDirectoryName(webcam.FilePath)!, Path.GetFileName(part.FilePath!)));
            var layer = new WebcamLayer { FilePath = webcamPath, X = webcam.X, Y = webcam.Y, Size = webcam.Size, Hidden = webcam.Hidden };

            string partial = webcamPath + PartialSuffix;
            try
            {
                await CutAsync(webcam.FilePath, partial, start, length, null);
                await EnsureFileReady(partial);
                TimeSpan webcamDuration = await FFmpegService.GetVideoDuration(partial);
                if (webcamDuration <= TimeSpan.Zero)
                    throw new Exception("Could not read the length of the cut webcam file");
                layer.Offset = Math.Round((cutDuration - webcamDuration).TotalSeconds, 3);
                return layer;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to cut the webcam for a match, leaving it out: {ex.Message}");
                TryDelete(partial);
                return null;
            }
        }

        private static async Task CutAsync(string input, string output, TimeSpan start, TimeSpan length, List<string>? audioTrackNames)
        {
            var args = new List<string>
            {
                "-y",
                "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", input,
                "-t", length.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-map", "0", "-map", "-0:d", "-c", "copy", "-map_chapters", "-1",
            };
            for (int i = 0; i < (audioTrackNames?.Count ?? 0); i++)
                args.AddRange([$"-metadata:s:a:{i}", $"title={audioTrackNames![i]}"]);
            args.AddRange(["-avoid_negative_ts", "make_zero", "-f", "mp4", output]);

            await FFmpegService.RunSimple(args);
            if (!File.Exists(output))
                throw new Exception($"ffmpeg did not create {output}");
        }

        private static string GetUniqueFilePath(string filePath)
        {
            string directory = Path.GetDirectoryName(filePath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(filePath);
            string extension = Path.GetExtension(filePath);
            string candidate = filePath;
            for (int i = 1; File.Exists(candidate); i++)
                candidate = PathUtils.Combine(directory, $"{name}_{i}{extension}");
            return candidate;
        }

        // From a split that was interrupted, e.g. by Segra closing
        private static void DeleteLeftoverPartials(string? folder)
        {
            if (folder == null || !Directory.Exists(folder))
                return;
            foreach (string path in Directory.EnumerateFiles(folder, "*" + PartialSuffix))
                TryDelete(path);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to delete {path}: {ex.Message}");
            }
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    }
}
