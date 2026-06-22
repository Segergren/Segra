using Serilog;
using System.Text.Json;
using Segra.Backend.App;
using Segra.Backend.Core;
using Segra.Backend.Games;
using Segra.Backend.Shared;
using Segra.Backend.Core.Models;
using Segra.Backend.Windows.Storage;

namespace Segra.Backend.Media
{
    internal class ContentService
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        public static async Task CreateMetadataFile(string filePath, Content.ContentType type, string game, List<Bookmark>? bookmarks = null, string? title = null, DateTime? createdAt = null, int? igdbId = null, bool isImported = false, List<string>? audioTrackNames = null)
        {
            bookmarks ??= [];
            filePath = PathUtils.Normalize(filePath);

            try
            {
                if (!File.Exists(filePath))
                {
                    Log.Information($"Video file not found: {filePath}");
                    return;
                }

                string contentFileName = Path.GetFileNameWithoutExtension(filePath);

                string metadataFolderPath = FolderNames.GetMetadataFolderPath(type);
                if (!Directory.Exists(metadataFolderPath))
                {
                    Directory.CreateDirectory(metadataFolderPath);
                }

                string metadataFilePath = PathUtils.Combine(metadataFolderPath, $"{contentFileName}.json");
                var (displaySize, sizeKb) = GetFileSize(filePath);

                var duration = await GetVideoDurationAsync(filePath);
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
                    Duration = duration,
                    AudioTrackNames = audioTrackNames,
                    IgdbId = igdbId,
                    IsImported = isImported
                };

                string metadataJson = JsonSerializer.Serialize(metadataContent, _jsonOptions);

                await File.WriteAllTextAsync(metadataFilePath, metadataJson);
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

                string metadataJson = await File.ReadAllTextAsync(metadataFilePath);
                var content = JsonSerializer.Deserialize<Content>(metadataJson);

                if (content == null)
                {
                    Log.Error($"Failed to deserialize metadata: {metadataFilePath}");
                    return null;
                }

                updateAction(content);

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

        public static async Task SyncContentGameNamesByIgdb()
        {
            var list = AppState.Instance.Content;
            if (list == null || list.Count == 0) return;

            int changed = await ReconcileGameNamesByIgdb(list);
            if (changed > 0)
            {
                AppState.Instance.SetContent(list, sendToFrontend: true);
            }
        }

        public static async Task<int> ReconcileGameNamesByIgdb(List<Content> contents)
        {
            if (contents == null || contents.Count == 0) return 0;

            var idToName = GameUtils.GetIgdbIdToNameMap();
            if (idToName.Count == 0) return 0;

            int changedCount = 0;
            int errorCount = 0;

            foreach (var content in contents)
            {
                try
                {
                    if (content.IgdbId is not int igdbId) continue;
                    if (!idToName.TryGetValue(igdbId, out string? canonicalName) || string.IsNullOrEmpty(canonicalName)) continue;
                    if (string.Equals(content.Game, canonicalName, StringComparison.Ordinal)) continue;

                    string newSanitized = StorageService.SanitizeGameNameForFolder(canonicalName);

                    string? newFilePath = null;
                    bool moved = false;

                    if (!string.IsNullOrEmpty(content.FilePath) && File.Exists(content.FilePath))
                    {
                        string? currentDir = Path.GetDirectoryName(content.FilePath);
                        string? typeFolder = currentDir != null ? Path.GetDirectoryName(currentDir) : null;

                        if (!string.IsNullOrEmpty(typeFolder))
                        {
                            string newDir = PathUtils.Combine(typeFolder, newSanitized);
                            string candidatePath = PathUtils.Combine(newDir, Path.GetFileName(content.FilePath));

                            bool sameAsCurrent = string.Equals(
                                Path.GetFullPath(candidatePath),
                                Path.GetFullPath(content.FilePath),
                                StringComparison.OrdinalIgnoreCase);

                            if (!sameAsCurrent)
                            {
                                if (File.Exists(candidatePath))
                                {
                                    Log.Warning("Skipping move for {File}: destination already exists at {Dest}", content.FilePath, candidatePath);
                                }
                                else
                                {
                                    Directory.CreateDirectory(newDir);
                                    File.Move(content.FilePath, candidatePath);
                                    newFilePath = candidatePath;
                                    moved = true;
                                    Log.Information("Moved content for IGDB {Id}: {Old} -> {New}", igdbId, content.FilePath, candidatePath);
                                }
                            }
                        }
                    }

                    string sidecar = PathUtils.Combine(FolderNames.GetMetadataFolderPath(content.Type), content.FileName + ".json");
                    await UpdateMetadataFile(sidecar, c =>
                    {
                        c.Game = canonicalName;
                        if (moved && newFilePath != null) c.FilePath = newFilePath;
                    });

                    content.Game = canonicalName;
                    if (moved && newFilePath != null) content.FilePath = newFilePath;

                    changedCount++;
                }
                catch (Exception ex)
                {
                    errorCount++;
                    Log.Error(ex, "Failed reconciling game name for content {File}", content?.FilePath ?? "(unknown)");
                }
            }

            if (changedCount > 0 || errorCount > 0)
            {
                Log.Information("ReconcileGameNamesByIgdb: changed={Changed}, errors={Errors}", changedCount, errorCount);
            }

            return changedCount;
        }

        public static async Task CreateThumbnail(string filePath, Content.ContentType type)
        {
            try
            {
                string contentFileName = Path.GetFileNameWithoutExtension(filePath);

                string thumbnailsFolderPath = FolderNames.GetThumbnailsFolderPath(type);
                if (!Directory.Exists(thumbnailsFolderPath))
                {
                    Directory.CreateDirectory(thumbnailsFolderPath);
                }

                string thumbnailFilePath = PathUtils.Combine(thumbnailsFolderPath, $"{contentFileName}.jpeg");

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

        public static async Task CreateWaveformFile(string videoFilePath, Content.ContentType type)
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

                string waveformFolderPath = FolderNames.GetWaveformsFolderPath(type);
                if (!Directory.Exists(waveformFolderPath))
                {
                    Directory.CreateDirectory(waveformFolderPath);
                }

                string tempPcmPath = PathUtils.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pcm");
                string waveformJsonPathTemp = PathUtils.Combine(waveformFolderPath, $"{contentFileName}.peaks.temp.json");
                string waveformJsonPath = PathUtils.Combine(waveformFolderPath, $"{contentFileName}.peaks.json");

                // Decode audio to raw mono 16-bit PCM at a modest sample rate for efficiency.
                // Probe the file for its audio track count so multi-track recordings can be
                // mixed together rather than ffmpeg silently picking just one stream.
                int sampleRate = 11025;
                var audioTrackNames = await Mp4BoxReader.ReadAudioTrackNamesAsync(videoFilePath);
                int audioStreamCount = audioTrackNames?.Count ?? 1;
                await FFmpegService.ExtractPcmAudio(videoFilePath, tempPcmPath, sampleRate, audioStreamCount);

                if (!File.Exists(tempPcmPath))
                {
                    Log.Error("PCM extraction did not produce output file.");
                    return;
                }

                // Read PCM and compute min/max pairs as 8-bit integers similar to audiowaveform output
                byte[] pcmBytes = await File.ReadAllBytesAsync(tempPcmPath);
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
                    await File.WriteAllTextAsync(waveformJsonPathTemp, JsonSerializer.Serialize(emptyJson));
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
                    for (int s = i; s < end; s++)
                    {
                        int byteIndex = s * 2;
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
                var json = JsonSerializer.Serialize(wrapper);
                await File.WriteAllTextAsync(waveformJsonPathTemp, json);
                File.Move(waveformJsonPathTemp, waveformJsonPath, true);
                Log.Information($"Waveform JSON successfully created at: {waveformJsonPath}");

                try { File.Delete(tempPcmPath); } catch { /* ignore */ }
            }
            catch (Exception ex)
            {
                Log.Error($"Error creating waveform JSON: {ex.Message}");
            }
        }

        public static async Task<TimeSpan> GetVideoDurationAsync(string videoFilePath)
        {
            try
            {
                return await FFmpegService.GetVideoDuration(videoFilePath);
            }
            catch (Exception ex)
            {
                Log.Error($"Error getting video duration: {ex.Message}");
                return TimeSpan.Zero;
            }
        }

        public static async Task DeleteContent(string filePath, Content.ContentType type, bool sendToFrontend = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    Log.Warning("DeleteClip called with an invalid file path.");
                    return;
                }

                string normalizedFilePath = PathUtils.Normalize(Path.GetFullPath(filePath));

                string? videoDirectory = Path.GetDirectoryName(normalizedFilePath);
                if (File.Exists(normalizedFilePath))
                {
                    int maxRetries = 3;
                    for (int i = 0; i < maxRetries; i++)
                    {
                        try
                        {
                            File.Delete(normalizedFilePath);
                            Log.Information($"Video file deleted: {normalizedFilePath}");
                            break;
                        }
                        catch (IOException)
                        {
                            if (i == maxRetries - 1) throw;
                            Log.Warning($"File is locked, retrying deletion in 500ms... (Attempt {i + 1}/{maxRetries})");
                            await Task.Delay(500);
                        }
                    }

                    if (!string.IsNullOrEmpty(videoDirectory) && Directory.Exists(videoDirectory))
                    {
                        try
                        {
                            // Only delete if the folder is empty and is a game subfolder (not the root video type folder)
                            string contentRoot = Settings.Instance.ContentFolder;
                            string[] rootFolders = { FolderNames.Sessions, FolderNames.Buffers, FolderNames.Clips, FolderNames.Highlights, FolderNames.Lowlights };
                            bool isGameSubfolder = rootFolders.Any(rf =>
                                videoDirectory.StartsWith(Path.Combine(contentRoot, rf), StringComparison.OrdinalIgnoreCase) &&
                                !videoDirectory.Equals(Path.Combine(contentRoot, rf), StringComparison.OrdinalIgnoreCase));

                            if (isGameSubfolder && !Directory.EnumerateFileSystemEntries(videoDirectory).Any())
                            {
                                Directory.Delete(videoDirectory);
                                Log.Information($"Deleted empty game folder: {videoDirectory}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"Failed to clean up empty game folder: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Log.Warning($"Video file not found (already deleted?): {normalizedFilePath}");
                }

                string contentFileName = Path.GetFileNameWithoutExtension(normalizedFilePath);

                string metadataFolderPath = FolderNames.GetMetadataFolderPath(type);
                string metadataFilePath = PathUtils.Combine(metadataFolderPath, $"{contentFileName}.json");
                if (File.Exists(metadataFilePath))
                {
                    File.Delete(metadataFilePath);
                    Log.Information($"Metadata file deleted: {metadataFilePath}");
                }
                else
                {
                    Log.Warning($"Metadata file not found: {metadataFilePath}");
                }

                string thumbnailsFolderPath = FolderNames.GetThumbnailsFolderPath(type);
                string thumbnailFilePath = PathUtils.Combine(thumbnailsFolderPath, $"{contentFileName}.jpeg");
                if (File.Exists(thumbnailFilePath))
                {
                    File.Delete(thumbnailFilePath);
                    Log.Information($"Thumbnail file deleted: {thumbnailFilePath}");
                }
                else
                {
                    Log.Warning($"Thumbnail file not found: {thumbnailFilePath}");
                }

                string waveformFolderPath = FolderNames.GetWaveformsFolderPath(type);
                string waveformFilePath = PathUtils.Combine(waveformFolderPath, $"{contentFileName}.peaks.json");
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
                await SettingsService.LoadContentFromFolderIntoState(sendToFrontend);
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

        public static async Task HandleAddBookmark(JsonElement message)
        {
            try
            {
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

                    BookmarkType bookmarkType = BookmarkType.Manual;
                    if (!string.IsNullOrEmpty(bookmarkTypeStr) && Enum.TryParse<BookmarkType>(bookmarkTypeStr, out var parsedType))
                    {
                        bookmarkType = parsedType;
                    }

                    if (!Enum.TryParse<Content.ContentType>(contentTypeStr, out Content.ContentType contentType))
                    {
                        Log.Error($"Invalid content type: {contentTypeStr}");
                        return;
                    }

                    string contentFileName = Path.GetFileNameWithoutExtension(filePath);
                    string metadataFolderPath = FolderNames.GetMetadataFolderPath(contentType);
                    string metadataFilePath = PathUtils.Combine(metadataFolderPath, $"{contentFileName}.json");

                    var bookmark = new Bookmark
                    {
                        Id = bookmarkId,
                        Type = bookmarkType,
                        Time = TimeSpan.Parse(timeString)
                    };

                    var content = await UpdateMetadataFile(metadataFilePath, c =>
                    {
                        c.Bookmarks ??= [];
                        c.AddBookmark(bookmark);
                    });

                    if (content == null)
                    {
                        return;
                    }

                    // Update the bookmark in the in-memory content collection
                    var contentItem = AppState.Instance.Content.FirstOrDefault(c =>
                        c.FilePath == filePath &&
                        c.Type.ToString() == contentTypeStr);

                    if (contentItem == null)
                    {
                        Log.Error($"Content item not found for {filePath} and {contentTypeStr}");
                        return;
                    }

                    contentItem.AddBookmark(bookmark);

                    await MessageService.SendStateToFrontend("Added bookmark");
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

                    if (!Enum.TryParse<Content.ContentType>(contentTypeStr, out Content.ContentType contentType))
                    {
                        Log.Error($"Invalid content type: {contentTypeStr}");
                        return;
                    }

                    string contentFileName = Path.GetFileNameWithoutExtension(filePath);
                    string metadataFolderPath = FolderNames.GetMetadataFolderPath(contentType);
                    string metadataFilePath = PathUtils.Combine(metadataFolderPath, $"{contentFileName}.json");

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
                    var contentItem = AppState.Instance.Content.FirstOrDefault(c =>
                        c.FilePath == filePath &&
                        c.Type.ToString() == contentTypeStr);

                    if (contentItem != null && contentItem.Bookmarks != null)
                    {
                        contentItem.Bookmarks = contentItem.Bookmarks.Where(b => b.Id != bookmarkId).ToList();
                    }

                    await MessageService.SendStateToFrontend("Deleted bookmark");
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

                if (message.TryGetProperty("FileName", out JsonElement fileNameElement) &&
                    message.TryGetProperty("ContentType", out JsonElement contentTypeElement) &&
                    message.TryGetProperty("Title", out JsonElement titleElement))
                {
                    string fileName = fileNameElement.GetString()!;
                    string contentTypeStr = contentTypeElement.GetString()!;
                    string newTitle = titleElement.GetString()!;

                    if (!Enum.TryParse(contentTypeStr, true, out Content.ContentType contentType))
                    {
                        Log.Error($"Invalid ContentType provided: {contentTypeStr}");
                        return;
                    }

                    string metadataFolderPath = FolderNames.GetMetadataFolderPath(contentType);
                    string metadataFilePath = PathUtils.Combine(metadataFolderPath, $"{fileName}.json");

                    if (!File.Exists(metadataFilePath))
                    {
                        Log.Error($"Metadata file not found: {metadataFilePath}");
                        return;
                    }

                    string metadataJson = await File.ReadAllTextAsync(metadataFilePath);
                    var currentContent = JsonSerializer.Deserialize<Content>(metadataJson);
                    if (currentContent == null)
                    {
                        Log.Error($"Failed to deserialize metadata: {metadataFilePath}");
                        return;
                    }

                    string newFileName = fileName;
                    string newFilePath = currentContent.FilePath;

                    string targetFileName = string.IsNullOrWhiteSpace(newTitle)
                        ? (currentContent.CreatedAt.AddTicks(-(currentContent.CreatedAt.Ticks % TimeSpan.TicksPerSecond)) - currentContent.Duration).ToString("yyyy-MM-dd_HH-mm-ss")
                        : SanitizeFileName(newTitle);

                    if (!string.IsNullOrWhiteSpace(targetFileName))
                    {
                        string currentFilePath = currentContent.FilePath;
                        string dir = PathUtils.Normalize(Path.GetDirectoryName(currentFilePath) ?? string.Empty);
                        string ext = Path.GetExtension(currentFilePath);
                        string candidatePath = PathUtils.Combine(dir, $"{targetFileName}{ext}");

                        if (!string.Equals(candidatePath, PathUtils.Normalize(currentFilePath), StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(candidatePath))
                            {
                                int counter = 1;
                                do
                                {
                                    candidatePath = PathUtils.Combine(dir, $"{targetFileName} ({counter}){ext}");
                                    counter++;
                                } while (File.Exists(candidatePath));
                            }

                            if (File.Exists(currentFilePath))
                            {
                                File.Move(currentFilePath, candidatePath);
                                newFilePath = candidatePath;
                                newFileName = Path.GetFileNameWithoutExtension(candidatePath);
                                Log.Information($"Renamed video file to {candidatePath}");

                                string thumbnailsFolderPath = FolderNames.GetThumbnailsFolderPath(contentType);
                                string oldThumbnailPath = PathUtils.Combine(thumbnailsFolderPath, $"{fileName}.jpeg");
                                if (File.Exists(oldThumbnailPath))
                                {
                                    File.Move(oldThumbnailPath, PathUtils.Combine(thumbnailsFolderPath, $"{newFileName}.jpeg"));
                                    Log.Information($"Renamed thumbnail for {newFileName}");
                                }

                                string waveformsFolderPath = FolderNames.GetWaveformsFolderPath(contentType);
                                string oldWaveformPath = PathUtils.Combine(waveformsFolderPath, $"{fileName}.peaks.json");
                                if (File.Exists(oldWaveformPath))
                                {
                                    File.Move(oldWaveformPath, PathUtils.Combine(waveformsFolderPath, $"{newFileName}.peaks.json"));
                                    Log.Information($"Renamed waveform for {newFileName}");
                                }
                            }
                        }
                    }

                    currentContent.Title = newTitle;
                    currentContent.FileName = newFileName;
                    currentContent.FilePath = newFilePath;
                    string updatedJson = JsonSerializer.Serialize(currentContent, _jsonOptions);

                    if (newFileName != fileName)
                    {
                        File.Delete(metadataFilePath);
                        string newMetadataFilePath = PathUtils.Combine(metadataFolderPath, $"{newFileName}.json");
                        await File.WriteAllTextAsync(newMetadataFilePath, updatedJson);
                        Log.Information($"Renamed metadata file to {newMetadataFilePath}");
                    }
                    else
                    {
                        await File.WriteAllTextAsync(metadataFilePath, updatedJson);
                    }

                    Log.Information($"Updated title for {fileName} to '{newTitle}'");
                    await SettingsService.LoadContentFromFolderIntoState(true);
                    await MessageService.SendStateToFrontend("Renamed content");
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

        private static string SanitizeFileName(string title)
        {
            var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new System.Text.StringBuilder();
            foreach (char c in title)
            {
                if (!invalidChars.Contains(c))
                    sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
