using System.Globalization;
using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Services;
using Serilog;

namespace Segra.Backend.Media
{
    /// <summary>
    /// Service for creating highlight videos from bookmarks using fast stream copy.
    /// </summary>
    public static class HighlightService
    {
        /// <summary>
        /// Creates a highlight video from all Kill bookmarks in a session.
        /// Uses stream copy for fast extraction without re-encoding.
        /// </summary>
        public static async Task CreateHighlightFromKills(string fileName, Action<int, string>? progressCallback = null)
        {
            try
            {
                Log.Information($"Starting highlight creation for: {fileName}");

                Content? content = Settings.Instance.State.Content.FirstOrDefault(x => x.FileName == fileName);
                if (content == null)
                {
                    Log.Warning($"No content found matching fileName: {fileName}");
                    return;
                }

                // Get all Kill bookmarks
                List<Bookmark> killBookmarks = content.Bookmarks
                    .Where(b => b.Type == BookmarkType.Kill)
                    .OrderBy(b => b.Time)
                    .ToList();

                if (killBookmarks.Count == 0)
                {
                    Log.Information($"No kill bookmarks found for: {fileName}");
                    progressCallback?.Invoke(-1, "No kills found in this session");
                    return;
                }

                Log.Information($"Found {killBookmarks.Count} kill bookmarks to include in highlight");
                progressCallback?.Invoke(5, $"Found {killBookmarks.Count} kills");

                // Convert bookmarks to time segments (4 seconds before and after each kill)
                var segments = killBookmarks.Select(b => new TimeSegment
                {
                    StartTime = Math.Max(0, b.Time.TotalSeconds - 4),
                    EndTime = b.Time.TotalSeconds + 4
                }).ToList();

                // Merge overlapping segments
                var mergedSegments = MergeOverlappingSegments(segments);
                Log.Information($"Merged {segments.Count} segments into {mergedSegments.Count} clips");

                // Create the highlight
                string videoFolder = Settings.Instance.ContentFolder;
                string inputFilePath = Path.Combine(videoFolder, content.Type.ToString().ToLower() + "s", $"{content.FileName}.mp4");

                if (!File.Exists(inputFilePath))
                {
                    Log.Error($"Input video file not found: {inputFilePath}");
                    progressCallback?.Invoke(-1, "Source video not found");
                    return;
                }

                string outputFolder = Path.Combine(videoFolder, "highlights");
                Directory.CreateDirectory(outputFolder);

                string outputFileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
                string outputFilePath = Path.Combine(outputFolder, outputFileName);

                progressCallback?.Invoke(10, "Extracting clips...");

                // Extract and concatenate segments using stream copy
                bool success = await ExtractAndConcatenateSegments(
                    inputFilePath,
                    outputFilePath,
                    mergedSegments,
                    (progress, message) => progressCallback?.Invoke(10 + (int)(progress * 80), message)
                );

                if (!success || !File.Exists(outputFilePath))
                {
                    Log.Error("Failed to create highlight video");
                    progressCallback?.Invoke(-1, "Failed to create highlight");
                    return;
                }

                progressCallback?.Invoke(92, "Creating metadata...");

                // Create metadata, thumbnail, and waveform
                await ContentService.CreateMetadataFile(outputFilePath, Content.ContentType.Highlight, content.Game!, null, content.Title, igdbId: content.IgdbId);

                progressCallback?.Invoke(95, "Creating thumbnail...");
                await ContentService.CreateThumbnail(outputFilePath, Content.ContentType.Highlight);

                progressCallback?.Invoke(98, "Creating waveform...");
                await ContentService.CreateWaveformFile(outputFilePath, Content.ContentType.Highlight);

                // Reload content
                await SettingsService.LoadContentFromFolderIntoState();

                progressCallback?.Invoke(100, "Done");
                Log.Information($"Highlight created successfully: {outputFilePath}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error creating highlight for {fileName}");
                progressCallback?.Invoke(-1, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts multiple segments from a video and concatenates them using stream copy.
        /// This is a fast operation as it doesn't re-encode the video.
        /// </summary>
        /// <param name="inputFilePath">Path to the source video file</param>
        /// <param name="outputFilePath">Path for the output video file</param>
        /// <param name="segments">List of time segments to extract</param>
        /// <param name="progressCallback">Optional callback for progress updates (0.0 to 1.0)</param>
        /// <returns>True if successful, false otherwise</returns>
        public static async Task<bool> ExtractAndConcatenateSegments(
            string inputFilePath,
            string outputFilePath,
            List<TimeSegment> segments,
            Action<double, string>? progressCallback = null)
        {
            if (!FFmpegService.FFmpegExists())
            {
                Log.Error($"FFmpeg executable not found");
                return false;
            }

            if (segments.Count == 0)
            {
                Log.Warning("No segments provided for extraction");
                return false;
            }

            List<string> tempFiles = new List<string>();
            string? concatFilePath = null;

            try
            {
                double totalDuration = segments.Sum(s => s.EndTime - s.StartTime);
                double processedDuration = 0;

                // Extract each segment to a temp file using stream copy
                for (int i = 0; i < segments.Count; i++)
                {
                    var segment = segments[i];
                    string tempFile = Path.Combine(Path.GetTempPath(), $"highlight_segment_{Guid.NewGuid()}.mp4");
                    double segmentDuration = segment.EndTime - segment.StartTime;

                    progressCallback?.Invoke(processedDuration / totalDuration, $"Extracting clip {i + 1} of {segments.Count}");

                    string arguments =
                        $"-y -ss {segment.StartTime.ToString(CultureInfo.InvariantCulture)} " +
                        $"-t {segmentDuration.ToString(CultureInfo.InvariantCulture)} " +
                        $"-i \"{inputFilePath}\" " +
                        $"-c copy -avoid_negative_ts make_zero \"{tempFile}\"";

                    await FFmpegService.RunSimple(arguments);

                    if (!File.Exists(tempFile))
                    {
                        Log.Error($"Failed to extract segment {i + 1}");
                        continue;
                    }

                    tempFiles.Add(tempFile);
                    processedDuration += segmentDuration;
                }

                if (tempFiles.Count == 0)
                {
                    Log.Error("No segments were successfully extracted");
                    return false;
                }

                progressCallback?.Invoke(0.9, "Combining clips...");

                // If only one segment, just move it to output
                if (tempFiles.Count == 1)
                {
                    File.Move(tempFiles[0], outputFilePath, overwrite: true);
                    tempFiles.Clear();
                    return true;
                }

                // Create concat file for multiple segments
                concatFilePath = Path.Combine(Path.GetTempPath(), $"highlight_concat_{Guid.NewGuid()}.txt");
                var concatLines = tempFiles.Select(f => $"file '{f.Replace("\\", "/").Replace("'", "'\\''")}'");
                await File.WriteAllLinesAsync(concatFilePath, concatLines);

                // Concatenate all segments using stream copy
                string concatArguments = $"-y -f concat -safe 0 -i \"{concatFilePath}\" -c copy -movflags +faststart \"{outputFilePath}\"";
                await FFmpegService.RunSimple(concatArguments);

                progressCallback?.Invoke(1.0, "Done");
                return File.Exists(outputFilePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error extracting and concatenating segments");
                return false;
            }
            finally
            {
                // Cleanup temp files
                foreach (var tempFile in tempFiles)
                {
                    try { File.Delete(tempFile); }
                    catch { /* ignore cleanup errors */ }
                }

                if (!string.IsNullOrEmpty(concatFilePath))
                {
                    try { File.Delete(concatFilePath); }
                    catch { /* ignore cleanup errors */ }
                }
            }
        }

        /// <summary>
        /// Merges overlapping time segments into continuous segments.
        /// </summary>
        private static List<TimeSegment> MergeOverlappingSegments(List<TimeSegment> segments)
        {
            if (segments.Count == 0) return new List<TimeSegment>();

            var sorted = segments.OrderBy(s => s.StartTime).ToList();
            var merged = new List<TimeSegment>();

            var current = new TimeSegment
            {
                StartTime = sorted[0].StartTime,
                EndTime = sorted[0].EndTime
            };

            for (int i = 1; i < sorted.Count; i++)
            {
                var next = sorted[i];

                // Check if segments overlap or are adjacent
                if (current.EndTime >= next.StartTime)
                {
                    // Extend current segment
                    current.EndTime = Math.Max(current.EndTime, next.EndTime);
                }
                else
                {
                    // No overlap, save current and start new
                    merged.Add(current);
                    current = new TimeSegment
                    {
                        StartTime = next.StartTime,
                        EndTime = next.EndTime
                    };
                }
            }

            merged.Add(current);
            return merged;
        }
    }

    /// <summary>
    /// Represents a time segment in a video.
    /// </summary>
    public class TimeSegment
    {
        public double StartTime { get; set; }
        public double EndTime { get; set; }
    }
}
