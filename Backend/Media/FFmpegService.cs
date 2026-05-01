using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;

namespace Segra.Backend.Media
{
    public class FFmpegException : Exception
    {
        public int ExitCode { get; }

        public FFmpegException(int exitCode)
            : base($"FFmpeg process exited with non-zero exit code: {exitCode}")
        {
            ExitCode = exitCode;
        }
    }

    public static class FFmpegErrors
    {
        // FFmpeg-specific AVERROR constants from libavutil/error.h.
        // FFERRTAG packs four bytes: -(a | b<<8 | c<<16 | d<<24).
        public const int AVERROR_BSF_NOT_FOUND = -(0xF8 | ('B' << 8) | ('S' << 16) | ('F' << 24));
        public const int AVERROR_BUG = -('B' | ('U' << 8) | ('G' << 16) | ('!' << 24));
        public const int AVERROR_BUFFER_TOO_SMALL = -('B' | ('U' << 8) | ('F' << 16) | ('S' << 24));
        public const int AVERROR_DECODER_NOT_FOUND = -(0xF8 | ('D' << 8) | ('E' << 16) | ('C' << 24));
        public const int AVERROR_DEMUXER_NOT_FOUND = -(0xF8 | ('D' << 8) | ('E' << 16) | ('M' << 24));
        public const int AVERROR_ENCODER_NOT_FOUND = -(0xF8 | ('E' << 8) | ('N' << 16) | ('C' << 24));
        public const int AVERROR_EOF = -('E' | ('O' << 8) | ('F' << 16) | (' ' << 24));
        public const int AVERROR_EXIT = -('E' | ('X' << 8) | ('I' << 16) | ('T' << 24));
        public const int AVERROR_EXTERNAL = -('E' | ('X' << 8) | ('T' << 16) | (' ' << 24));
        public const int AVERROR_FILTER_NOT_FOUND = -(0xF8 | ('F' << 8) | ('I' << 16) | ('L' << 24));
        public const int AVERROR_INVALIDDATA = -('I' | ('N' << 8) | ('D' << 16) | ('A' << 24));
        public const int AVERROR_MUXER_NOT_FOUND = -(0xF8 | ('M' << 8) | ('U' << 16) | ('X' << 24));
        public const int AVERROR_OPTION_NOT_FOUND = -(0xF8 | ('O' << 8) | ('P' << 16) | ('T' << 24));
        public const int AVERROR_PATCHWELCOME = -('P' | ('A' << 8) | ('W' << 16) | ('E' << 24));
        public const int AVERROR_PROTOCOL_NOT_FOUND = -(0xF8 | ('P' << 8) | ('R' << 16) | ('O' << 24));
        public const int AVERROR_STREAM_NOT_FOUND = -(0xF8 | ('S' << 8) | ('T' << 16) | ('R' << 24));
        public const int AVERROR_BUG2 = -('B' | ('U' << 8) | ('G' << 16) | (' ' << 24));
        public const int AVERROR_UNKNOWN = -('U' | ('N' << 8) | ('K' << 16) | ('N' << 24));
        public const int AVERROR_EXPERIMENTAL = -0x2BB2AFA8;
        public const int AVERROR_INPUT_CHANGED = -0x636E6701;
        public const int AVERROR_OUTPUT_CHANGED = -0x636E6702;
        public const int AVERROR_HTTP_BAD_REQUEST = -(0xF8 | ('4' << 8) | ('0' << 16) | ('0' << 24));
        public const int AVERROR_HTTP_UNAUTHORIZED = -(0xF8 | ('4' << 8) | ('0' << 16) | ('1' << 24));
        public const int AVERROR_HTTP_FORBIDDEN = -(0xF8 | ('4' << 8) | ('0' << 16) | ('3' << 24));
        public const int AVERROR_HTTP_NOT_FOUND = -(0xF8 | ('4' << 8) | ('0' << 16) | ('4' << 24));
        public const int AVERROR_HTTP_TOO_MANY_REQUESTS = -(0xF8 | ('4' << 8) | ('2' << 16) | ('9' << 24));
        public const int AVERROR_HTTP_OTHER_4XX = -(0xF8 | ('4' << 8) | ('X' << 16) | ('X' << 24));
        public const int AVERROR_HTTP_SERVER_ERROR = -(0xF8 | ('5' << 8) | ('X' << 16) | ('X' << 24));

        private const string BugReportSuffix =
            "\n\nThis is likely a bug. Please report it on our Discord or on GitHub:\nhttps://github.com/Segergren/Segra/issues";

        public static (string Message, bool LikelyBug) Describe(int exitCode)
        {
            return exitCode switch
            {
                // POSIX errno (AVERROR(errno) = -errno)
                -1 => ("Segra was not permitted to perform the operation.", false),
                -2 => ("A required file could not be found. It may have been moved or deleted while the operation was running.", false),
                -5 => ("A read or write error occurred while accessing your drive.", false),
                -11 => ("The system was temporarily busy. Please try again in a moment.", false),
                -12 => ("Segra ran out of memory while processing the video.", false),
                -13 => ("Access to a file was denied. It may be locked by another program (e.g. antivirus or a video player).", false),
                -16 => ("A file could not be accessed because it is currently in use by another program.", false),
                -17 => ("An existing file was in the way and could not be replaced.", false),
                -22 => ("The video could not be processed. The source file may be corrupted or in an unsupported format.", false),
                -24 => ("Too many files are open. Please restart Segra and try again.", false),
                -28 => ("Ran out of disk space while writing temporary files. Free up space on your system drive (C:) and try again.", false),
                -30 => ("The output destination is read-only and cannot be written to.", false),
                -32 => ("The video processing tool was closed unexpectedly before finishing.", false),

                // FFmpeg-specific
                AVERROR_INVALIDDATA => ("The source video appears to be corrupted or in an unexpected format.", false),
                AVERROR_EOF => ("The source video ended sooner than expected.", false),
                AVERROR_EXIT => ("The operation was cancelled before it could finish.", false),
                AVERROR_BUG or AVERROR_BUG2 => ("The video processing tool reported an internal bug.", true),
                AVERROR_UNKNOWN => ("An unknown error occurred while processing the video.", true),
                AVERROR_EXTERNAL => ("An error occurred in a component used by the video processor.", true),
                AVERROR_BUFFER_TOO_SMALL => ("The video data did not fit into an internal buffer.", true),
                AVERROR_BSF_NOT_FOUND => ("A bitstream filter required for this operation is missing.", true),
                AVERROR_DECODER_NOT_FOUND => ("A video decoder required for this file is missing.", true),
                AVERROR_ENCODER_NOT_FOUND => ("A video encoder required for the output is missing.", true),
                AVERROR_MUXER_NOT_FOUND => ("A component required to produce the output format is missing.", true),
                AVERROR_DEMUXER_NOT_FOUND => ("A component required to read the input format is missing.", true),
                AVERROR_PROTOCOL_NOT_FOUND => ("A protocol handler required for this operation is missing.", true),
                AVERROR_FILTER_NOT_FOUND => ("A filter required for this operation is missing.", true),
                AVERROR_STREAM_NOT_FOUND => ("An expected video or audio stream was missing from the source file.", true),
                AVERROR_OPTION_NOT_FOUND => ("An invalid option was passed to the video processor.", true),
                AVERROR_PATCHWELCOME => ("This feature is not yet supported by the video processor.", true),
                AVERROR_INPUT_CHANGED => ("The input changed unexpectedly during processing.", true),
                AVERROR_OUTPUT_CHANGED => ("The output changed unexpectedly during processing.", true),
                AVERROR_EXPERIMENTAL => ("This file needs an experimental codec that is not enabled.", true),

                // HTTP (rare in this app, but cheap to cover)
                AVERROR_HTTP_BAD_REQUEST => ("The remote server rejected the request (HTTP 400).", false),
                AVERROR_HTTP_UNAUTHORIZED => ("The remote server requires authentication (HTTP 401).", false),
                AVERROR_HTTP_FORBIDDEN => ("The remote server denied access (HTTP 403).", false),
                AVERROR_HTTP_NOT_FOUND => ("The remote resource was not found (HTTP 404).", false),
                AVERROR_HTTP_TOO_MANY_REQUESTS => ("The remote server is rate-limiting requests (HTTP 429).", false),
                AVERROR_HTTP_OTHER_4XX => ("The remote server returned a client error (HTTP 4xx).", false),
                AVERROR_HTTP_SERVER_ERROR => ("The remote server returned a server error (HTTP 5xx).", false),

                _ => ($"Video processing failed unexpectedly (code {exitCode}).", true),
            };
        }

        public static string DescribeForUser(int exitCode)
        {
            var (message, likelyBug) = Describe(exitCode);
            return likelyBug ? message + BugReportSuffix : message;
        }
    }

    public static class FFmpegService
    {
        private const string FFmpegExecutable = "ffmpeg.exe";

        /// <summary>
        /// Gets the path to the ffmpeg executable and verifies it exists
        /// </summary>
        public static string GetFFmpegPath()
        {
            return FFmpegExecutable;
        }

        /// <summary>
        /// Checks if ffmpeg executable exists
        /// </summary>
        public static bool FFmpegExists()
        {
            return File.Exists(FFmpegExecutable);
        }

        /// <summary>
        /// Runs ffmpeg with progress tracking and callbacks
        /// </summary>
        public static async Task RunWithProgress(
            int processId,
            string arguments,
            double? totalDuration,
            Action<double> progressCallback,
            Action<Process>? onProcessStarted = null)
        {
            if (!FFmpegExists())
            {
                throw new FileNotFoundException($"FFmpeg executable not found at: {FFmpegExecutable}");
            }

            Log.Information($"[Process {processId}] Starting FFmpeg");
            Log.Information($"[Process {processId}] FFmpeg path: {FFmpegExecutable}");
            Log.Information($"[Process {processId}] FFmpeg arguments: {arguments}");

            var processStartInfo = new ProcessStartInfo
            {
                FileName = FFmpegExecutable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (var process = new Process { StartInfo = processStartInfo })
            {
                // Handle standard output (non-blocking)
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Log.Information($"[Process {processId}] FFmpeg stdout: {e.Data}");
                    }
                };

                // Handle standard error (non-blocking)
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;

                    // Log all FFmpeg stderr output
                    Log.Information($"[Process {processId}] FFmpeg stderr: {e.Data}");

                    try
                    {
                        // Only try to parse time if we have total duration
                        if (totalDuration.HasValue)
                        {
                            var timeMatch = Regex.Match(e.Data, @"time=(\d+:\d+:\d+\.\d+)");
                            if (timeMatch.Success)
                            {
                                var ts = TimeSpan.Parse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                                var progress = ts.TotalSeconds / totalDuration.Value;
                                progressCallback?.Invoke(progress);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[Process {processId}] Failed to parse FFmpeg progress: {ex.Message}");
                    }
                };

                try
                {
                    process.Start();
                    Log.Information($"[Process {processId}] FFmpeg process started (PID: {process.Id})");

                    // Notify caller that process has started (for tracking)
                    onProcessStarted?.Invoke(process);

                    // Begin async reading of both streams to prevent buffer blocking
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    await process.WaitForExitAsync();

                    Log.Information($"[Process {processId}] FFmpeg process completed with exit code: {process.ExitCode}");

                    if (process.ExitCode != 0)
                    {
                        throw new FFmpegException(process.ExitCode);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[Process {processId}] Error in FFmpeg process: {ex.Message}");
                    Log.Error($"[Process {processId}] Stack trace: {ex.StackTrace}");
                    throw;
                }
            }
        }

        /// <summary>
        /// Runs ffmpeg without progress tracking (simple execution)
        /// </summary>
        public static async Task RunSimple(IEnumerable<string> arguments)
        {
            if (!FFmpegExists())
            {
                throw new FileNotFoundException($"FFmpeg executable not found at: {FFmpegExecutable}");
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = FFmpegExecutable,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in arguments)
            {
                processStartInfo.ArgumentList.Add(arg);
            }

            Log.Information("Running simple ffmpeg with arguments: {Arguments}", string.Join(" ", processStartInfo.ArgumentList));

            using (var process = new Process { StartInfo = processStartInfo })
            {
                try
                {
                    // Attach event handlers before starting
                    process.OutputDataReceived += (sender, e) => { };
                    process.ErrorDataReceived += (sender, e) => { };

                    process.Start();

                    // Begin async reading to prevent buffer deadlock
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    await process.WaitForExitAsync();

                    Log.Information($"FFmpeg process completed with exit code: {process.ExitCode}");

                    if (process.ExitCode != 0)
                    {
                        throw new FFmpegException(process.ExitCode);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Error in FFmpeg process: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// Runs ffmpeg and returns stdout as byte array (useful for piping images)
        /// </summary>
        public static async Task<byte[]> RunAndCaptureOutput(IEnumerable<string> arguments)
        {
            if (!FFmpegExists())
            {
                throw new FileNotFoundException($"FFmpeg executable not found at: {FFmpegExecutable}");
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = FFmpegExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in arguments)
            {
                processInfo.ArgumentList.Add(arg);
            }

            using (var ffmpegProcess = new Process { StartInfo = processInfo })
            {
                ffmpegProcess.Start();

                // Read streams concurrently to prevent deadlocks from full buffers, using BaseStream for raw binary
                using var ms = new MemoryStream();
                var stdoutTask = ffmpegProcess.StandardOutput.BaseStream.CopyToAsync(ms);
                var stderrTask = ffmpegProcess.StandardError.ReadToEndAsync();

                await Task.WhenAll(stdoutTask, stderrTask);
                await ffmpegProcess.WaitForExitAsync();

                string ffmpegStdErr = stderrTask.Result;

                if (ffmpegProcess.ExitCode != 0)
                {
                    Log.Error("FFmpeg error (exit={ExitCode}). Stderr={StdErr}", ffmpegProcess.ExitCode, ffmpegStdErr);
                    throw new Exception($"FFmpeg error: {ffmpegStdErr}");
                }

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Runs ffmpeg to get metadata and returns stderr output
        /// </summary>
        public static async Task<string> GetMetadata(string inputFilePath)
        {
            if (!FFmpegExists())
            {
                throw new FileNotFoundException($"FFmpeg executable not found at: {FFmpegExecutable}");
            }

            if (!File.Exists(inputFilePath))
            {
                throw new FileNotFoundException($"Input file not found at: {inputFilePath}");
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = FFmpegExecutable,
                Arguments = $"-i \"{inputFilePath}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                string output = await process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
                return output;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                throw new TimeoutException($"FFmpeg metadata read timed out for: {inputFilePath}");
            }
        }

        /// <summary>
        /// Extracts video duration from ffmpeg metadata output
        /// </summary>
        public static TimeSpan ExtractDuration(string ffmpegOutput)
        {
            const string durationKeyword = "Duration: ";
            int startIndex = ffmpegOutput.IndexOf(durationKeyword);
            if (startIndex != -1)
            {
                startIndex += durationKeyword.Length;
                int endIndex = ffmpegOutput.IndexOf(",", startIndex);
                if (endIndex != -1)
                {
                    string durationString = ffmpegOutput.Substring(startIndex, endIndex - startIndex).Trim();
                    if (TimeSpan.TryParse(durationString, out var duration))
                    {
                        return duration;
                    }
                }
            }
            return TimeSpan.Zero;
        }

        private static readonly Regex _streamHeaderRegex = new(
            @"^\s*Stream #\d+:\d+[^\n]*?:\s*(\w+):",
            RegexOptions.Compiled);

        private static readonly Regex _metadataTagRegex = new(
            @"^\s+(\w+)\s*:\s*(.+?)\s*$",
            RegexOptions.Compiled);

        private static readonly HashSet<string> _genericHandlerNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "SoundHandler",
            "OBS Audio Handler"
        };

        /// <summary>
        /// Extracts per-track audio names from the stderr output of `ffmpeg -i`.
        /// Used as a fallback for non-OBS MP4s that carry standard `title` or
        /// non-generic `handler_name` tags. Entries are null for tracks with
        /// no usable tag, so callers can decide how to label them. Returns
        /// null if fewer than two audio streams are present.
        /// </summary>
        public static List<string?>? ExtractAudioTrackNames(string ffmpegOutput)
        {
            if (string.IsNullOrEmpty(ffmpegOutput))
            {
                return null;
            }

            var tracks = new List<string?>();
            bool inAudioStream = false;
            string? currentTitle = null;
            string? currentHandler = null;

            void Flush()
            {
                if (!inAudioStream) return;
                string? resolved = null;
                if (!string.IsNullOrWhiteSpace(currentTitle))
                {
                    resolved = currentTitle;
                }
                else if (!string.IsNullOrWhiteSpace(currentHandler) && !_genericHandlerNames.Contains(currentHandler!))
                {
                    resolved = currentHandler;
                }
                tracks.Add(resolved);
                inAudioStream = false;
                currentTitle = null;
                currentHandler = null;
            }

            foreach (var rawLine in ffmpegOutput.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');

                var streamMatch = _streamHeaderRegex.Match(line);
                if (streamMatch.Success)
                {
                    Flush();
                    string streamType = streamMatch.Groups[1].Value;
                    if (streamType.Equals("Audio", StringComparison.OrdinalIgnoreCase))
                    {
                        inAudioStream = true;
                    }
                    continue;
                }

                if (!inAudioStream) continue;

                var tagMatch = _metadataTagRegex.Match(line);
                if (!tagMatch.Success) continue;

                string key = tagMatch.Groups[1].Value;
                string value = tagMatch.Groups[2].Value;
                if (key.Equals("title", StringComparison.OrdinalIgnoreCase))
                {
                    currentTitle = value;
                }
                else if (key.Equals("handler_name", StringComparison.OrdinalIgnoreCase))
                {
                    currentHandler = value;
                }
            }

            Flush();

            return tracks.Count >= 2 ? tracks : null;
        }

        /// <summary>
        /// Gets video duration from a file
        /// </summary>
        public static async Task<TimeSpan> GetVideoDuration(string videoFilePath, int maxRetries = 3, int delayMs = 2000)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    string metadata = await GetMetadata(videoFilePath);
                    var duration = ExtractDuration(metadata);
                    if (duration != TimeSpan.Zero)
                    {
                        return duration;
                    }

                    Log.Warning($"Could not extract duration from {videoFilePath} (attempt {attempt}/{maxRetries}). FFmpeg output: {metadata.Substring(0, Math.Min(500, metadata.Length))}");
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error getting video duration for {videoFilePath} (attempt {attempt}/{maxRetries}): {ex.Message}");
                }

                if (attempt < maxRetries)
                {
                    await Task.Delay(delayMs);
                }
            }

            Log.Error($"Failed to get video duration for {videoFilePath} after {maxRetries} attempts");
            return TimeSpan.Zero;
        }

        /// <summary>
        /// Generates a thumbnail from a video at a specific timestamp
        /// </summary>
        public static async Task<byte[]> GenerateThumbnail(string inputFilePath, double timeSeconds, int width = 320)
        {
            string timeString = timeSeconds.ToString(CultureInfo.InvariantCulture);
            var args = new[]
            {
                "-y",
                "-ss", timeString,
                "-i", inputFilePath,
                "-frames:v", "1",
                "-vf", $"scale={width}:-1",
                "-f", "image2pipe",
                "-vcodec", "mjpeg",
                "-q:v", "20",
                "pipe:1"
            };
            return await RunAndCaptureOutput(args);
        }

        /// <summary>
        /// Generates a thumbnail from a video at the midpoint
        /// </summary>
        public static async Task CreateThumbnailFile(string inputFilePath, string outputFilePath, int width = 720, int quality = 9)
        {
            TimeSpan duration = await GetVideoDuration(inputFilePath);

            if (duration == TimeSpan.Zero)
            {
                throw new Exception("Video duration is not available.");
            }

            // Calculate the midpoint
            TimeSpan midpoint = TimeSpan.FromTicks(duration.Ticks / 2);
            string midpointTime = midpoint.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

            var arguments = new[]
            {
                "-ss", midpointTime,
                "-i", inputFilePath,
                "-vf", $"scale={width}:-1",
                "-qscale:v", quality.ToString(CultureInfo.InvariantCulture),
                "-vframes", "1",
                outputFilePath
            };
            await RunSimple(arguments);
        }

        /// <summary>
        /// Extracts audio as PCM data for waveform generation. When the input has
        /// multiple audio streams, mixes them all together via amix so the waveform
        /// represents every audio device, not just whichever stream ffmpeg picks
        /// by default.
        /// </summary>
        public static async Task ExtractPcmAudio(string inputFilePath, string outputPcmPath, int sampleRate = 11025, int audioStreamCount = 1)
        {
            var args = new List<string>
            {
                "-i", inputFilePath,
                "-vn",
            };

            if (audioStreamCount > 1)
            {
                var inputs = string.Concat(Enumerable.Range(0, audioStreamCount).Select(i => $"[0:a:{i}]"));
                args.Add("-filter_complex");
                args.Add($"{inputs}amix=inputs={audioStreamCount}:normalize=0[aout]");
                args.Add("-map");
                args.Add("[aout]");
            }

            args.AddRange(new[]
            {
                "-ac", "1",
                "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
                "-f", "s16le",
                "-acodec", "pcm_s16le",
                outputPcmPath
            });

            await RunSimple(args);
        }
    }
}
