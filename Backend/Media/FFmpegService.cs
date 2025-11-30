using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;

namespace Segra.Backend.Media
{
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
                        throw new Exception($"FFmpeg process failed with exit code: {process.ExitCode}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[Process {processId}] Error in FFmpeg process: {ex.Message}");
                    Log.Error($"[Process {processId}] Stack trace: {ex.StackTrace}");
                    throw;
                }
            }

            // Always make sure we report completion
            Log.Information($"[Process {processId}] Reporting final progress: 100%");
            progressCallback?.Invoke(1.0);
        }

        /// <summary>
        /// Runs ffmpeg without progress tracking (simple execution)
        /// </summary>
        public static async Task RunSimple(string arguments)
        {
            if (!FFmpegExists())
            {
                throw new FileNotFoundException($"FFmpeg executable not found at: {FFmpegExecutable}");
            }

            Log.Information("Running simple ffmpeg with arguments: " + arguments);

            var processStartInfo = new ProcessStartInfo
            {
                FileName = FFmpegExecutable,
                Arguments = arguments,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

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
                        throw new Exception($"FFmpeg process exited with non-zero exit code: {process.ExitCode}");
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
        public static async Task<byte[]> RunAndCaptureOutput(string arguments)
        {
            if (!FFmpegExists())
            {
                throw new FileNotFoundException($"FFmpeg executable not found at: {FFmpegExecutable}");
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = FFmpegExecutable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

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
            string arguments = $"-y -ss {timeString} -i \"{inputFilePath}\" -frames:v 1 -vf scale={width}:-1 -f image2pipe -vcodec mjpeg -q:v 20 pipe:1";
            return await RunAndCaptureOutput(arguments);
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

            string arguments = $"-ss {midpointTime} -i \"{inputFilePath}\" -vf \"scale={width}:-1\" -qscale:v {quality} -vframes 1 \"{outputFilePath}\"";
            await RunSimple(arguments);
        }

        /// <summary>
        /// Extracts audio as PCM data for waveform generation
        /// </summary>
        public static async Task ExtractPcmAudio(string inputFilePath, string outputPcmPath, int sampleRate = 11025)
        {
            string arguments = $"-i \"{inputFilePath}\" -vn -ac 1 -ar {sampleRate} -f s16le -acodec pcm_s16le \"{outputPcmPath}\"";
            await RunSimple(arguments);
        }
    }
}
