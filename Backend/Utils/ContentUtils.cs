using Segra.Backend.Models;
using Serilog;
using System.Diagnostics;
using System.Globalization;

namespace Segra.Backend.Utils
{
    internal class ContentUtils
    {
        public static void CreateMetadataFile(string filePath, Content.ContentType type, string game, List<Bookmark>? bookmarks = null)
        {
            bookmarks ??= new List<Bookmark>();

            try
            {
                // Ensure the video file exists
                if (!File.Exists(filePath))
                {
                    Log.Information($"Video file not found: {filePath}");
                    return;
                }

                // Get the directory and file name
                string contentFileName = Path.GetFileNameWithoutExtension(filePath);

                // Ensure the .metadata folder exists
                string metadataFolderPath = Path.Combine(Settings.Instance.ContentFolder, ".metadata", type.ToString().ToLower() + "s");
                if (!Directory.Exists(metadataFolderPath))
                {
                    DirectoryInfo dir = Directory.CreateDirectory(metadataFolderPath);
                    dir.Attributes |= FileAttributes.Hidden;
                }

                // Create the metadata file
                string metadataFilePath = Path.Combine(metadataFolderPath, $"{contentFileName}.json");
                var metadataContent = new Content
                {
                    Type = type,
                    Title = string.Empty,
                    Game = game,
                    Bookmarks = bookmarks,
                    FileName = contentFileName,
                    FilePath = filePath,
                    FileSize = GetFileSize(filePath),
                    CreatedAt = DateTime.Now,
                    Duration = GetVideoDuration(filePath)
                };

                string metadataJson = System.Text.Json.JsonSerializer.Serialize(metadataContent, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(metadataFilePath, metadataJson);
                Log.Information($"Metadata file created at: {metadataFilePath}");
            }
            catch (Exception ex)
            {
                Log.Error($"Error creating metadata file: {ex.Message}");
            }
        }

        public static void CreateThumbnail(string filePath, Content.ContentType type)
        {
            try
            {
                // Get the directory and file name
                string contentFileName = Path.GetFileNameWithoutExtension(filePath);

                // Ensure the .thumbnails folder exists
                string thumbnailsFolderPath = Path.Combine(Settings.Instance.ContentFolder, ".thumbnails", type.ToString().ToLower() + "s");
                if (!Directory.Exists(thumbnailsFolderPath))
                {
                    DirectoryInfo dir = Directory.CreateDirectory(thumbnailsFolderPath);
                    dir.Attributes |= FileAttributes.Hidden;
                }

                // Define the output thumbnail file path
                string thumbnailFilePath = Path.Combine(thumbnailsFolderPath, $"{contentFileName}.jpeg");

                // Locate the bundled FFmpeg executable
                string ffmpegPath = "ffmpeg.exe";
                if (!File.Exists(ffmpegPath))
                {
                    Log.Information("FFmpeg binary not found in Resources/FFmpeg!");
                    return;
                }

                // Ensure the duration is available
                TimeSpan duration = GetVideoDuration(filePath);

                if (duration == TimeSpan.Zero)
                {
                    // Handle the case where the duration is not available
                    throw new Exception("Video duration is not available.");
                }

                // Calculate the midpoint
                TimeSpan midpoint = TimeSpan.FromTicks(duration.Ticks / 2);

                string midpointTime = midpoint.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

                // FFmpeg arguments to extract a thumbnail at 5 seconds
                string ffmpegArgs = $"-ss {midpointTime} -i \"{filePath}\" -vf \"scale=720:-1\" -qscale:v 9 -vframes 1 \"{thumbnailFilePath}\"";

                // Run FFmpeg as a process
                ProcessStartInfo processInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = ffmpegArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (Process process = new Process { StartInfo = processInfo })
                {
                    process.Start();

                    // Capture output (for debugging or logging)
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();

                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        Log.Error($"FFmpeg error: {error}");
                        Log.Error("Thumbnail generation failed.");
                    }
                    else
                    {
                        Log.Information($"Thumbnail successfully created at: {thumbnailFilePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error creating thumbnail: {ex.Message}");
            }
        }
        public static TimeSpan GetVideoDuration(string videoFilePath)
        {
            string ffmpegPath = "ffmpeg.exe";

            try
            {
                if (!File.Exists(ffmpegPath))
                    throw new FileNotFoundException($"FFmpeg executable not found at: {ffmpegPath}");
                if (!File.Exists(videoFilePath))
                    throw new FileNotFoundException($"Video file not found at: {videoFilePath}");

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-i \"{videoFilePath}\"",
                    RedirectStandardError = true, // FFmpeg outputs metadata to standard error
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = processStartInfo })
                {
                    process.Start();
                    string output = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    // Extract duration from FFmpeg output
                    string? durationLine = ExtractDuration(output);
                    if (TimeSpan.TryParse(durationLine, out var duration))
                    {
                        return duration;
                    }

                    throw new Exception("Unable to parse duration from FFmpeg output.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting video duration: {ex.Message}");
                return TimeSpan.Zero; // Return zero duration in case of error
            }
        }

        public static void DeleteContent(string filePath, Content.ContentType type)
        {
            try
            {
                // Validate the file path
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    Log.Warning("DeleteClip called with an invalid file path.");
                    return;
                }

                // Normalize the file path
                string normalizedFilePath = Path.GetFullPath(filePath);

                // Ensure the video file exists before attempting deletion
                if (File.Exists(normalizedFilePath))
                {
                    File.Delete(normalizedFilePath);
                    Log.Information($"Video file deleted: {normalizedFilePath}");
                }
                else
                {
                    Log.Warning($"Video file not found (already deleted?): {normalizedFilePath}");
                }

                // Extract the content file name without extension
                string contentFileName = Path.GetFileNameWithoutExtension(normalizedFilePath);

                // Construct the metadata file path
                string metadataFolderPath = Path.Combine(Settings.Instance.ContentFolder, ".metadata", type.ToString().ToLower() + "s");
                string metadataFilePath = Path.Combine(metadataFolderPath, $"{contentFileName}.json");

                // Delete the metadata file if it exists
                if (File.Exists(metadataFilePath))
                {
                    File.Delete(metadataFilePath);
                    Log.Information($"Metadata file deleted: {metadataFilePath}");
                }
                else
                {
                    Log.Warning($"Metadata file not found: {metadataFilePath}");
                }

                // Construct the thumbnail file path
                string thumbnailsFolderPath = Path.Combine(Settings.Instance.ContentFolder, ".thumbnails", type.ToString().ToLower() + "s");
                string thumbnailFilePath = Path.Combine(thumbnailsFolderPath, $"{contentFileName}.jpeg");

                // Delete the thumbnail file if it exists
                if (File.Exists(thumbnailFilePath))
                {
                    File.Delete(thumbnailFilePath);
                    Log.Information($"Thumbnail file deleted: {thumbnailFilePath}");
                }
                else
                {
                    Log.Warning($"Thumbnail file not found: {thumbnailFilePath}");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error($"Access denied while deleting files: {ex.Message}");
            }
            catch (IOException ex)
            {
                Log.Error($"I/O error while deleting files: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Error($"Unexpected error while deleting clip: {ex.Message}");
            }
            finally
            {
                SettingsUtils.LoadContentFromFolderIntoState();
            }
        }

        private static string? ExtractDuration(string ffmpegOutput)
        {
            // Look for a line like: "Duration: 00:02:34.56, start: 0.000000, bitrate: 128 kb/s"
            const string durationKeyword = "Duration: ";
            int startIndex = ffmpegOutput.IndexOf(durationKeyword);
            if (startIndex != -1)
            {
                startIndex += durationKeyword.Length;
                int endIndex = ffmpegOutput.IndexOf(",", startIndex);
                if (endIndex != -1)
                {
                    return ffmpegOutput.Substring(startIndex, endIndex - startIndex).Trim();
                }
            }
            return null;
        }

        private static string GetFileSize(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                double fileSizeInMb = fileInfo.Length / (1024.0 * 1024.0);

                if (fileSizeInMb > 1000)
                {
                    double fileSizeInGb = fileSizeInMb / 1024.0;
                    return $"{fileSizeInGb:F2} GB";
                }
                else
                {
                    return $"{fileSizeInMb:F2} MB";
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error getting file size: {ex.Message}");
                return "Unknown";
            }
        }
    }
}
