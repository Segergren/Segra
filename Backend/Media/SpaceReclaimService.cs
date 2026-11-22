using Serilog;
using System.Diagnostics;
using System.Text.Json;
using Segra.Backend.App;
using Segra.Backend.Core;
using Segra.Backend.Core.Models;
using Segra.Backend.Shared;

namespace Segra.Backend.Media
{
    // Rewrites sessions whose index was trimmed, so the unused bytes before the cut are freed. It waits until
    // nothing is recording and the video isn't open, and runs again after a restart.
    internal static class SpaceReclaimService
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
        private const int MaxAttempts = 3;
        private static readonly object _lock = new();
        private static List<string>? _queue;
        private static int _running;
        // Failed rewrites per file in this run, so one bad file isn't copied over and over
        private static readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

        private static string QueuePath => PathUtils.Combine(FolderNames.CacheFolder, "reclaim-queue.json");

        public static void Enqueue(string filePath)
        {
            lock (_lock)
            {
                var queue = LoadQueue();
                string path = PathUtils.Normalize(filePath);
                if (!queue.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    queue.Add(path);
                    SaveQueue(queue);
                }
            }
            Start();
        }

        public static void Start()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            {
                _ = Task.Run(RunAsync);
            }
        }

        private static async Task RunAsync()
        {
            try
            {
                // Lets the library load after a restart before videos are looked up
                await Task.Delay(RetryDelay);
                while (true)
                {
                    string? path;
                    lock (_lock)
                        path = LoadQueue().FirstOrDefault();
                    if (path == null)
                        return;

                    if (IsBusy(path))
                    {
                        await Task.Delay(RetryDelay);
                        continue;
                    }

                    if (await RewriteAsync(path))
                    {
                        lock (_lock)
                        {
                            var queue = LoadQueue();
                            queue.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                            SaveQueue(queue);
                        }
                        _failures.Remove(path);
                    }
                    else
                    {
                        await Task.Delay(RetryDelay);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Reclaiming space from trimmed sessions failed");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        // The copy would compete with a recording for the disk, and Windows can't replace an open file
        private static bool IsBusy(string path) =>
            AppState.Instance.Recording != null || AppState.Instance.IsFinishingRecording || MessageService.IsViewing(path);

        // Returns true when the file is done with, including when it is gone or can't be rewritten
        private static async Task<bool> RewriteAsync(string path)
        {
            var content = AppState.Instance.Content.FirstOrDefault(c => string.Equals(PathUtils.Normalize(c.FilePath), path, StringComparison.OrdinalIgnoreCase));
            if (content == null || !File.Exists(path))
            {
                Log.Information($"Skipping space reclaim for {path}: the session is gone");
                return true;
            }

            string temp = path + ".reclaim";
            var args = new List<string> { "-y", "-i", path, "-map", "0", "-map", "-0:d", "-c", "copy", "-map_chapters", "-1" };
            for (int i = 0; i < (content.AudioTrackNames?.Count ?? 0); i++)
                args.AddRange([$"-metadata:s:a:{i}", $"title={content.AudioTrackNames![i]}"]);
            args.AddRange(["-f", "mp4", temp]);

            Log.Information($"Reclaiming the unused space in {path}");
            var stopwatch = Stopwatch.StartNew();
            bool busy = false;
            bool replaced = await EmbeddedMetadataService.RunExclusiveAsync(async () =>
            {
                try
                {
                    await FFmpegService.RunSimple(args);
                    TimeSpan before = await FFmpegService.GetVideoDuration(path);
                    TimeSpan after = await FFmpegService.GetVideoDuration(temp);
                    if (after <= TimeSpan.Zero || Math.Abs((after - before).TotalSeconds) > 1)
                    {
                        Log.Warning($"Rewritten copy of {path} is {after} long instead of {before}, keeping the original");
                        TryDelete(temp);
                        return false;
                    }

                    // A recording or the player may have started meanwhile
                    if (IsBusy(path))
                    {
                        busy = true;
                        TryDelete(temp);
                        return false;
                    }
                    File.SetCreationTimeUtc(temp, File.GetCreationTimeUtc(path));
                    File.Move(temp, path, true);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not reclaim the space in {path}, retrying later: {ex.Message}");
                    TryDelete(temp);
                    return false;
                }
            });
            if (!replaced)
                return !busy && GiveUp(path);

            var (displaySize, sizeKb) = ContentService.GetFileSize(path);
            await ContentService.UpdateMetadataFile(FolderNames.GetMetadataFilePath(content.Type, content.Id), c =>
            {
                c.FileSize = displaySize;
                c.FileSizeKb = sizeKb;
            });
            await EmbeddedMetadataService.EmbedAsync(content);
            await SettingsService.LoadContentFromFolderIntoState(false);
            Log.Information($"Reclaimed the unused space in {path} in {stopwatch.Elapsed.TotalSeconds:F0} s ({displaySize})");
            return true;
        }

        // True once the file has failed MaxAttempts times, which drops it from the queue
        private static bool GiveUp(string path)
        {
            int failures = _failures.GetValueOrDefault(path) + 1;
            _failures[path] = failures;
            if (failures < MaxAttempts)
                return false;
            Log.Warning($"Giving up on reclaiming the space in {path} after {failures} failed attempts");
            return true;
        }

        private static List<string> LoadQueue()
        {
            if (_queue != null)
                return _queue;
            try
            {
                _queue = File.Exists(QueuePath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(QueuePath)) ?? [] : [];
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not read {QueuePath}: {ex.Message}");
                _queue = [];
            }
            return _queue;
        }

        private static void SaveQueue(List<string> queue)
        {
            try
            {
                File.WriteAllText(QueuePath, JsonSerializer.Serialize(queue));
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not save {QueuePath}: {ex.Message}");
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
