using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Games;
using Segra.Backend.Services;
using Serilog;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Segra.Backend.Media
{
    internal class ContentService
    {
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static void CreateMetadataFile(string filePath, Content.ContentType type, string game, List<Bookmark>? bookmarks = null, string? title = null, DateTime? createdAt = null)
        {
            bookmarks ??= [];

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
                var (displaySize, sizeKb) = GetFileSize(filePath);

                // Build audio track names: Track 1 is Full Mix, then one per audio source (inputs then outputs), up to OBS's 6 total tracks
                var trackNames = new List<string>
                {
                    "Full Mix"
                };
                try
                {
                    if (Settings.Instance.EnableSeparateAudioTracks)
                    {
                        var perSourceNames = new List<string>();
                        if (Settings.Instance.InputDevices != null)
                            perSourceNames.AddRange(Settings.Instance.InputDevices.Select(d => d.Name));
                        if (Settings.Instance.OutputDevices != null)
                            perSourceNames.AddRange(Settings.Instance.OutputDevices.Select(d => d.Name));

                        // OBS supports 6 tracks total; we already used 1 for the mix
                        foreach (var name in perSourceNames.Take(Math.Max(0, 5)))
                        {
                            trackNames.Add(name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to build audio track names for metadata: {ex.Message}");
                }

                var metadataContent = new Content
                {
                    Type = type,
                    Title = title ?? string.Empty,
                    Game = game,
                    Bookmarks = bookmarks,
                    FileName = contentFileName,
                    FilePath = filePath,
                    FileSize = displaySize,
                    FileSizeKb = sizeKb,
                    CreatedAt = createdAt ?? DateTime.Now,
                    Duration = GetVideoDuration(filePath),
                    AudioTrackNames = trackNames
                };

                string metadataJson = JsonSerializer.Serialize(metadataContent, _jsonOptions);

                File.WriteAllText(metadataFilePath, metadataJson);
                Log.Information($"Metadata file created at: {metadataFilePath}");
            }
            catch (Exception ex)
            {
                Log.Error($"Error creating metadata file: {ex.Message}");
            }
        }

        public static async Task<Content?> UpdateMetadataFile(string metadataFilePath, Action<Content> updateAction)
        {
            try
            {
                if (!File.Exists(metadataFilePath))
                {
                    Log.Error($"Metadata file not found: {metadataFilePath}");
                    return null;
                }

                // Read and deserialize
                string metadataJson = await File.ReadAllTextAsync(metadataFilePath);
                var content = System.Text.Json.JsonSerializer.Deserialize<Content>(metadataJson);

                if (content == null)
                {
                    Log.Error($"Failed to deserialize metadata: {metadataFilePath}");
                    return null;
                }

                // Apply the update
                updateAction(content);

                // Serialize and write back
                string updatedJson = JsonSerializer.Serialize(content, _jsonOptions);

                await File.WriteAllTextAsync(metadataFilePath, updatedJson);

                return content;
            }
            catch (Exception ex)
            {
                Log.Error($"Error updating metadata file {metadataFilePath}: {ex.Message}");
                return null;
            }
        }

        public static async Task CreateThumbnail(string filePath, Content.ContentType type)
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

                if (!FFmpegService.FFmpegExists())
                {
                    Log.Information("FFmpeg binary not found!");
                    return;
                }

                await FFmpegService.CreateThumbnailFile(filePath, thumbnailFilePath);
                Log.Information($"Thumbnail successfully created at: {thumbnailFilePath}");
            }
            catch (Exception ex)
            {
                Log.Error($"Error creating thumbnail: {ex.Message}");
            }
        }

        public static async void CreateWaveformFile(string videoFilePath, Content.ContentType type)
        {
            try
            {
                if (!FFmpegService.FFmpegExists())
                {
                    Log.Error($"FFmpeg executable not found at: {FFmpegService.GetFFmpegPath()}");
                    return;
                }
                if (!File.Exists(videoFilePath))
                {
                    Log.Error($"Video file not found at: {videoFilePath}");
                    return;
                }

                string contentFileName = Path.GetFileNameWithoutExtension(videoFilePath);

                // Ensure the .waveforms folder exists and is hidden
                string waveformFolderPath = Path.Combine(Settings.Instance.ContentFolder, ".waveforms", type.ToString().ToLower() + "s");
                if (!Directory.Exists(waveformFolderPath))
                {
                    DirectoryInfo dir = Directory.CreateDirectory(waveformFolderPath);
                    dir.Attributes |= FileAttributes.Hidden;
                }

                string tempPcmPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pcm");
                string waveformJsonPathTemp = Path.Combine(waveformFolderPath, $"{contentFileName}.peaks.temp.json");
                string waveformJsonPath = Path.Combine(waveformFolderPath, $"{contentFileName}.peaks.json");

                // Decode audio to raw mono 16-bit PCM at a modest sample rate for efficiency
                int sampleRate = 11025;
                await FFmpegService.ExtractPcmAudio(videoFilePath, tempPcmPath, sampleRate);

                if (!File.Exists(tempPcmPath))
                {
                    Log.Error("PCM extraction did not produce output file.");
                    return;
                }

                // Read PCM and compute min/max pairs as 8-bit integers similar to audiowaveform output
                byte[] pcmBytes = File.ReadAllBytes(tempPcmPath);
                int totalSamples = pcmBytes.Length / 2; // 16-bit mono
                if (totalSamples == 0)
                {
                    Log.Warning("No audio samples found when generating waveform peaks.");
                    var emptyJson = new
                    {
                        version = 2,
                        channels = 1,
                        sample_rate = sampleRate,
                        samples_per_pixel = 1,
                        bits = 8,
                        length = 0,
                        data = Array.Empty<int>()
                    };
                    File.WriteAllText(waveformJsonPathTemp, JsonSerializer.Serialize(emptyJson));
                    File.Move(waveformJsonPathTemp, waveformJsonPath, true);
                    return;
                }

                // Aim for ~50 pixel columns per second; each column contributes two values (min,max)
                double columnsPerSecond = 50.0;
                int columns = Math.Max(1, (int)Math.Round((totalSamples / (double)sampleRate) * columnsPerSecond));
                int samplesPerPixel = Math.Max(1, (int)Math.Ceiling(totalSamples / (double)columns));

                var data = new List<int>(columns * 2);

                for (int i = 0; i < totalSamples; i += samplesPerPixel)
                {
                    int end = Math.Min(totalSamples, i + samplesPerPixel);
                    short min16 = short.MaxValue;
                    short max16 = short.MinValue;
                    int byteIndex = i * 2;
                    for (int s = i; s < end; s++, byteIndex += 2)
                    {
                        short sample = BitConverter.ToInt16(pcmBytes, byteIndex);
                        if (sample < min16) min16 = sample;
                        if (sample > max16) max16 = sample;
                    }
                    // Scale 16-bit PCM to 8-bit range approximately -128..127
                    int min8 = (int)Math.Round(min16 / 256.0);
                    int max8 = (int)Math.Round(max16 / 256.0);
                    // Clamp to [-128,127]
                    min8 = Math.Max(-128, Math.Min(127, min8));
                    max8 = Math.Max(-128, Math.Min(127, max8));
                    data.Add(min8);
                    data.Add(max8);
                }

                var wrapper = new
                {
                    version = 2,
                    channels = 1,
                    sample_rate = sampleRate,
                    samples_per_pixel = samplesPerPixel,
                    bits = 8,
                    length = data.Count,
                    data
                };
                // Serialize JSON
                var json = JsonSerializer.Serialize(wrapper);
                File.WriteAllText(waveformJsonPathTemp, json);
                File.Move(waveformJsonPathTemp, waveformJsonPath, true);
                Log.Information($"Waveform JSON successfully created at: {waveformJsonPath}");

                // Cleanup
                try { File.Delete(tempPcmPath); } catch { /* ignore */ }
            }
            catch (Exception ex)
            {
                Log.Error($"Error creating waveform JSON: {ex.Message}");
            }
        }

        public static TimeSpan GetVideoDuration(string videoFilePath)
        {
            try
            {
                return FFmpegService.GetVideoDuration(videoFilePath).Result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting video duration: {ex.Message}");
                return TimeSpan.Zero; // Return zero duration in case of error
            }
        }

        public static async Task DeleteContent(string filePath, Content.ContentType type)
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

                // Construct the waveform JSON path
                string waveformFolderPath = Path.Combine(Settings.Instance.ContentFolder, ".waveforms", type.ToString().ToLower() + "s");
                string waveformFilePath = Path.Combine(waveformFolderPath, $"{contentFileName}.peaks.json");

                // Delete the waveform file if it exists
                if (File.Exists(waveformFilePath))
                {
                    File.Delete(waveformFilePath);
                    Log.Information($"Waveform file deleted: {waveformFilePath}");
                }
                else
                {
                    Log.Warning($"Waveform file not found: {waveformFilePath}");
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
                await SettingsService.LoadContentFromFolderIntoState();
            }
        }

        public static (string displaySize, long sizeKb) GetFileSize(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                long fileSizeInKb = fileInfo.Length / 1024;
                double fileSizeInMb = fileInfo.Length / (1024.0 * 1024.0);

                if (fileSizeInMb > 1000)
                {
                    double fileSizeInGb = fileSizeInMb / 1024.0;
                    return ($"{fileSizeInGb:F2} GB", fileSizeInKb);
                }
                else
                {
                    return ($"{fileSizeInMb:F2} MB", fileSizeInKb);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error getting file size: {ex.Message}");
                return ("Unknown", 0);
            }
        }

        public static (string gameName, DateTime? extractedDate) ParseFileNameInfo(string fileName)
        {
            string gameName = fileName; // Fallback to original filename
            DateTime? extractedDate = null;

            // Remove common prefixes and suffixes
            string cleanName = fileName
                .Replace("Highlight_", "")
                .Replace("Clip_", "")
                .Replace("Replay_", "")
                .Replace("Desktop_", "")
                .Replace("DesktopRecording", "Desktop")
                .Replace("Recording_", "");

            // Date patterns to try
            var datePatterns = new[]
            {
                @"(\d{4}-\d{2}-\d{2})",           // YYYY-MM-DD
                @"(\d{2}-\d{2}-\d{4})",           // MM-DD-YYYY or DD-MM-YYYY
                @"(\d{4}\.\d{2}\.\d{2})",         // YYYY.MM.DD
                @"(\d{2}\.\d{2}\.\d{4})",         // MM.DD.YYYY or DD.MM.YYYY
                @"(\d{4}_\d{2}_\d{2})",           // YYYY_MM_DD
                @"(\d{2}_\d{2}_\d{4})"            // MM_DD_YYYY or DD_MM_YYYY
            };

            // Try to extract date
            foreach (string pattern in datePatterns)
            {
                var match = Regex.Match(cleanName, pattern);
                if (match.Success)
                {
                    string dateStr = match.Groups[1].Value;

                    // Try different date formats
                    string[] formats = {
                        "yyyy-MM-dd", "MM-dd-yyyy", "dd-MM-yyyy",
                        "yyyy.MM.dd", "MM.dd.yyyy", "dd.MM.yyyy",
                        "yyyy_MM_dd", "MM_dd_yyyy", "dd_MM_yyyy"
                    };

                    foreach (string format in formats)
                    {
                        if (DateTime.TryParseExact(dateStr, format, null, DateTimeStyles.None, out DateTime parsedDate))
                        {
                            extractedDate = parsedDate;
                            break;
                        }
                    }

                    if (extractedDate.HasValue)
                    {
                        // Remove the date part from the name for game detection
                        cleanName = cleanName.Replace(dateStr, "").Trim('_', '-', '.', ' ');
                        break;
                    }
                }
            }

            // Remove time patterns
            var timePatterns = new[]
            {
                @"_\d{2}-\d{2}-\d{2}",    // _HH-MM-SS
                @"_\d{2}\.\d{2}\.\d{2}",  // _HH.MM.SS
                @"_\d{2}:\d{2}:\d{2}",    // _HH:MM:SS
                @"\s\d{2}-\d{2}-\d{2}",   // HH-MM-SS
                @"\s\d{2}\.\d{2}\.\d{2}", // HH.MM.SS
                @"\s\d{2}:\d{2}:\d{2}"    // HH:MM:SS
            };

            foreach (string pattern in timePatterns)
            {
                cleanName = Regex.Replace(cleanName, pattern, "");
            }

            // Clean up any remaining separators and common words
            cleanName = cleanName
                .Replace("_", " ")
                .Replace("-", " ")
                .Replace(".", " ")
                .Trim();

            // Remove common action words at the end
            var actionWords = new[] { "Ace", "Win", "Victory", "Goal", "Clutch", "Kill", "DoubleKill", "TripleKill",
                                    "BossFight", "Mission", "Match", "Explore", "Sabotage", "FullMatch", "Highlight",
                                    "Clip", "Replay", "Play", "Heist", "Recording", "Build", "SoloWin" };

            foreach (string action in actionWords)
            {
                if (cleanName.EndsWith(" " + action, StringComparison.OrdinalIgnoreCase))
                {
                    cleanName = cleanName.Substring(0, cleanName.Length - action.Length - 1).Trim();
                    break;
                }
            }

            // Smart game name parsing - use cleanName if meaningful, otherwise fallback to original fileName
            string nameToFormat = (!string.IsNullOrWhiteSpace(cleanName) &&
                                  !Regex.IsMatch(cleanName, @"^[\d\s_\-\.]+$"))
                                  ? cleanName : fileName;
            gameName = GameNameUtils.SmartFormatGameName(nameToFormat);

            return (gameName, extractedDate);
        }

        public static async Task HandleAddBookmark(JsonElement message)
        {
            try
            {
                // Get required properties from the message
                if (message.TryGetProperty("FilePath", out JsonElement filePathElement) &&
                    message.TryGetProperty("Type", out JsonElement typeElement) &&
                    message.TryGetProperty("Time", out JsonElement timeElement) &&
                    message.TryGetProperty("ContentType", out JsonElement contentTypeElement) &&
                    message.TryGetProperty("Id", out JsonElement idElement))
                {
                    string? filePath = filePathElement.GetString();
                    string? bookmarkTypeStr = typeElement.GetString();
                    string? timeString = timeElement.GetString();
                    string? contentTypeStr = contentTypeElement.GetString();
                    int bookmarkId = idElement.GetInt32();

                    if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(timeString) || string.IsNullOrEmpty(contentTypeStr))
                    {
                        Log.Error("Required parameters are null or empty in AddBookmark message");
                        return;
                    }

                    // Parse bookmark type, default to Manual if not valid
                    BookmarkType bookmarkType = BookmarkType.Manual;
                    if (!string.IsNullOrEmpty(bookmarkTypeStr) && Enum.TryParse<BookmarkType>(bookmarkTypeStr, out var parsedType))
                    {
                        bookmarkType = parsedType;
                    }

                    // Determine content type from the provided value
                    if (!Enum.TryParse<Content.ContentType>(contentTypeStr, out Content.ContentType contentType))
                    {
                        Log.Error($"Invalid content type: {contentTypeStr}");
                        return;
                    }

                    // Get metadata file path
                    string contentFileName = Path.GetFileNameWithoutExtension(filePath);
                    string metadataFolderPath = Path.Combine(Settings.Instance.ContentFolder, ".metadata", contentType.ToString().ToLower() + "s");
                    string metadataFilePath = Path.Combine(metadataFolderPath, $"{contentFileName}.json");

                    // Create a new bookmark
                    var bookmark = new Bookmark
                    {
                        Id = bookmarkId,
                        Type = bookmarkType,
                        Time = TimeSpan.Parse(timeString)
                    };

                    // Update the metadata file
                    var content = await UpdateMetadataFile(metadataFilePath, c =>
                    {
                        if (c.Bookmarks == null)
                        {
                            c.Bookmarks = [];
                        }
                        c.Bookmarks.Add(bookmark);
                    });

                    if (content == null)
                    {
                        return;
                    }

                    // Update the bookmark in the in-memory content collection
                    var contentItem = Settings.Instance?.State.Content.FirstOrDefault(c =>
                        c.FilePath == filePath &&
                        c.Type.ToString() == contentTypeStr);

                    if (contentItem == null)
                    {
                        Log.Error($"Content item not found for {filePath} and {contentTypeStr}");
                        return;
                    }

                    contentItem.Bookmarks.Add(bookmark);

                    await MessageService.SendSettingsToFrontend("Added bookmark");
                    Log.Information($"Added bookmark of type {bookmarkType} at {timeString} to {metadataFilePath}");
                }
                else
                {
                    Log.Error("Required properties missing in AddBookmark message.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error handling AddBookmark: {ex.Message}");
            }
        }

        public static async Task HandleDeleteBookmark(JsonElement message)
        {
            try
            {
                // Get required properties from the message
                if (message.TryGetProperty("FilePath", out JsonElement filePathElement) &&
                    message.TryGetProperty("ContentType", out JsonElement contentTypeElement) &&
                    message.TryGetProperty("Id", out JsonElement idElement))
                {
                    string? filePath = filePathElement.GetString();
                    string? contentTypeStr = contentTypeElement.GetString();
                    int bookmarkId = idElement.GetInt32();

                    if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(contentTypeStr))
                    {
                        Log.Error("Required parameters are null or empty in DeleteBookmark message");
                        return;
                    }

                    // Determine content type from the provided value
                    if (!Enum.TryParse<Content.ContentType>(contentTypeStr, out Content.ContentType contentType))
                    {
                        Log.Error($"Invalid content type: {contentTypeStr}");
                        return;
                    }

                    // Get metadata file path
                    string contentFileName = Path.GetFileNameWithoutExtension(filePath);
                    string metadataFolderPath = Path.Combine(Settings.Instance.ContentFolder, ".metadata", contentType.ToString().ToLower() + "s");
                    string metadataFilePath = Path.Combine(metadataFolderPath, $"{contentFileName}.json");

                    // Update the metadata file
                    var content = await UpdateMetadataFile(metadataFilePath, c =>
                    {
                        if (c.Bookmarks != null)
                        {
                            c.Bookmarks = c.Bookmarks.Where(b => b.Id != bookmarkId).ToList();
                        }
                    });

                    if (content == null)
                    {
                        return;
                    }

                    // Update the bookmark in the in-memory content collection
                    var contentItem = Settings.Instance?.State.Content.FirstOrDefault(c =>
                        c.FilePath == filePath &&
                        c.Type.ToString() == contentTypeStr);

                    if (contentItem != null && contentItem.Bookmarks != null)
                    {
                        contentItem.Bookmarks = contentItem.Bookmarks.Where(b => b.Id != bookmarkId).ToList();
                    }

                    await MessageService.SendSettingsToFrontend("Deleted bookmark");
                    Log.Information($"Deleted bookmark with id {bookmarkId} from {metadataFilePath}");
                }
                else
                {
                    Log.Error("Required properties missing in DeleteBookmark message.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error handling DeleteBookmark: {ex.Message}");
            }
        }

        public static async Task HandleRenameContent(JsonElement message)
        {
            try
            {
                Log.Information($"Handling RenameContent with message: {message}");

                // Extract FileName, ContentType, and NewName from the message
                if (message.TryGetProperty("FileName", out JsonElement fileNameElement) &&
                    message.TryGetProperty("ContentType", out JsonElement contentTypeElement) &&
                    message.TryGetProperty("Title", out JsonElement titleElement))
                {
                    string fileName = fileNameElement.GetString()!;
                    string contentTypeStr = contentTypeElement.GetString()!;
                    string newTitle = titleElement.GetString()!;

                    // Parse the content type
                    if (!Enum.TryParse(contentTypeStr, true, out Content.ContentType contentType))
                    {
                        Log.Error($"Invalid ContentType provided: {contentTypeStr}");
                        return;
                    }

                    // Construct metadata file path
                    string metadataFolderPath = Path.Combine(Settings.Instance.ContentFolder, ".metadata", contentType.ToString().ToLower() + "s");
                    string metadataFilePath = Path.Combine(metadataFolderPath, $"{fileName}.json");

                    // Update the metadata file
                    var content = await UpdateMetadataFile(metadataFilePath, c =>
                    {
                        c.Title = newTitle;
                    });

                    if (content == null)
                    {
                        return;
                    }

                    Log.Information($"Updated title for {fileName} to '{newTitle}' in metadata file: {metadataFilePath}");

                    // Reload content from disk to update in-memory state
                    await SettingsService.LoadContentFromFolderIntoState(true);

                    // Refresh the content in the frontend
                    await MessageService.SendSettingsToFrontend("Renamed content");
                }
                else
                {
                    Log.Error("FileName, ContentType, or Title property not found in RenameContent message.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error handling RenameContent: {ex.Message}");
            }
        }
    }
}
