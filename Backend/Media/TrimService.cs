using System.Globalization;
using System.Text.Json;
using Segra.Backend.App;
using Segra.Backend.Core;
using Segra.Backend.Core.Models;
using Segra.Backend.Shared;
using Serilog;

namespace Segra.Backend.Media
{
    internal static class TrimService
    {
        private const double MinimumTrimDurationSeconds = 0.1;

        public static async Task HandleTrimContent(JsonElement message)
        {
            string? sourceId = null;
            Content? source = null;
            string? workingOutputFilePath = null;
            string? backupFilePath = null;
            bool overwrite = false;
            bool originalReplaced = false;

            try
            {
                if (!message.TryGetProperty("Id", out JsonElement idElement) ||
                    !message.TryGetProperty("StartTime", out JsonElement startElement) ||
                    !message.TryGetProperty("EndTime", out JsonElement endElement))
                {
                    throw new ArgumentException("Trim requires a content id, start time, and end time.");
                }

                sourceId = idElement.GetString();
                double startTime = startElement.GetDouble();
                double endTime = endElement.GetDouble();
                overwrite = message.TryGetProperty("Overwrite", out JsonElement overwriteElement) &&
                    overwriteElement.ValueKind == JsonValueKind.True;

                if (string.IsNullOrWhiteSpace(sourceId) ||
                    !double.IsFinite(startTime) ||
                    !double.IsFinite(endTime))
                {
                    throw new ArgumentException("The trim range is invalid.");
                }

                source = AppState.Instance.Content.FirstOrDefault(c => c.Id == sourceId)
                    ?? throw new FileNotFoundException("The source video is no longer available.");

                if (!File.Exists(source.FilePath))
                {
                    throw new FileNotFoundException("The source video file could not be found.", source.FilePath);
                }

                double sourceDuration = source.Duration.TotalSeconds;
                startTime = Math.Clamp(startTime, 0, sourceDuration);
                endTime = Math.Clamp(endTime, 0, sourceDuration);
                double trimDuration = endTime - startTime;

                if (trimDuration < MinimumTrimDurationSeconds)
                {
                    throw new ArgumentException("The trim range must be at least 0.1 seconds long.");
                }

                string finalOutputFilePath = overwrite ? source.FilePath : GetUniqueOutputPath(source.FilePath);
                workingOutputFilePath = overwrite ? GetTemporaryOutputPath(source.FilePath) : finalOutputFilePath;

                string startArg = startTime.ToString("0.###", CultureInfo.InvariantCulture);
                string durationArg = trimDuration.ToString("0.###", CultureInfo.InvariantCulture);

                await MessageService.SendFrontendMessage("TrimProgress", new
                {
                    sourceId,
                    status = "trimming"
                });

                // Stream copy keeps trim fast avoids applying clip encoding settings.
                // Seeking before the input so FFmpeg starts at the nearest
                // usable keyframe without decoding the whole source.
                await FFmpegService.RunSimple(new[]
                {
                    "-y",
                    "-ss", startArg,
                    "-i", source.FilePath,
                    "-t", durationArg,
                    "-map", "0",
                    "-c", "copy",
                    "-avoid_negative_ts", "make_zero",
                    "-movflags", "+faststart",
                    workingOutputFilePath
                });

                if (overwrite)
                {
                    backupFilePath = GetBackupPath(source.FilePath);
                    File.Move(source.FilePath, backupFilePath);
                    try
                    {
                        File.Move(workingOutputFilePath, source.FilePath);
                        workingOutputFilePath = null;
                        originalReplaced = true;
                    }
                    catch
                    {
                        File.Move(backupFilePath, source.FilePath);
                        backupFilePath = null;
                        throw;
                    }
                }

                List<Bookmark> bookmarks = source.Bookmarks
                    .Where(bookmark => bookmark.Time.TotalSeconds >= startTime && bookmark.Time.TotalSeconds <= endTime)
                    .Select(bookmark => new Bookmark
                    {
                        Id = bookmark.Id,
                        Type = bookmark.Type,
                        Subtype = bookmark.Subtype,
                        Time = bookmark.Time - TimeSpan.FromSeconds(startTime),
                        AiRating = bookmark.AiRating
                    })
                    .ToList();

                string? trimmedId = await ContentService.CreateMetadataFile(
                    finalOutputFilePath,
                    source.Type,
                    source.Game,
                    bookmarks,
                    source.Title,
                    source.CreatedAt.AddSeconds(startTime),
                    source.IgdbId,
                    source.IsImported,
                    source.AudioTrackNames,
                    source.AudioTrackTypes,
                    source.Compressed,
                    source.GameExePath);

                if (string.IsNullOrEmpty(trimmedId))
                {
                    throw new IOException("The trimmed video was created, but its metadata could not be saved.");
                }

                await ContentService.CreateThumbnail(finalOutputFilePath, source.Type, trimmedId);
                await ContentService.CreateWaveformFile(finalOutputFilePath, source.Type, trimmedId);
                await SettingsService.LoadContentFromFolderIntoState(sendToFrontend: false);
                Content? trimmedContent = AppState.Instance.Content.FirstOrDefault(c => c.Id == trimmedId);
                if (!string.IsNullOrEmpty(backupFilePath))
                {
                    TryDelete(backupFilePath);
                    backupFilePath = null;
                }
                originalReplaced = false;
                await MessageService.SendStateToFrontend("Trimmed content");
                await MessageService.SendFrontendMessage("TrimProgress", new
                {
                    sourceId,
                    status = "done",
                    trimmedId,
                    overwritten = overwrite,
                    trimmedContent
                });
                string successMessage = overwrite
                    ? "The original video was replaced with the selected trim range."
                    : "A trimmed copy was saved beside the original video.";
                await MessageService.ShowModal("Trim complete", successMessage);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to trim content {ContentId}", sourceId ?? "(unknown)");

                if (originalReplaced && source != null && !string.IsNullOrEmpty(backupFilePath))
                {
                    try
                    {
                        TryDelete(source.FilePath);
                        File.Move(backupFilePath, source.FilePath);
                        originalReplaced = false;
                        backupFilePath = null;

                        string? restoredId = await ContentService.CreateMetadataFile(
                            source.FilePath,
                            source.Type,
                            source.Game,
                            source.Bookmarks,
                            source.Title,
                            source.CreatedAt,
                            source.IgdbId,
                            source.IsImported,
                            source.AudioTrackNames,
                            source.AudioTrackTypes,
                            source.Compressed,
                            source.GameExePath);
                        await ContentService.CreateThumbnail(source.FilePath, source.Type, restoredId);
                        await ContentService.CreateWaveformFile(source.FilePath, source.Type, restoredId);
                        await SettingsService.LoadContentFromFolderIntoState(sendToFrontend: false);
                        await MessageService.SendStateToFrontend("Restored original after failed trim");
                    }
                    catch (Exception restoreEx)
                    {
                        Log.Error(restoreEx, "Failed to restore original video from {BackupPath}", backupFilePath);
                    }
                }

                if (!string.IsNullOrEmpty(workingOutputFilePath))
                {
                    TryDelete(workingOutputFilePath);
                }

                await MessageService.SendFrontendMessage("TrimProgress", new
                {
                    sourceId,
                    status = "error"
                });
                await MessageService.ShowModal("Trim failed", ex.Message, "error");
            }
        }

        private static string GetUniqueOutputPath(string sourceFilePath)
        {
            string directory = Path.GetDirectoryName(sourceFilePath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(sourceFilePath);
            string extension = Path.GetExtension(sourceFilePath);
            string candidate = PathUtils.Combine(directory, $"{baseName}_trimmed{extension}");
            int suffix = 2;

            while (File.Exists(candidate))
            {
                candidate = PathUtils.Combine(directory, $"{baseName}_trimmed_{suffix}{extension}");
                suffix++;
            }

            return candidate;
        }

        private static string GetTemporaryOutputPath(string sourceFilePath)
        {
            string directory = Path.GetDirectoryName(sourceFilePath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(sourceFilePath);
            string extension = Path.GetExtension(sourceFilePath);
            return PathUtils.Combine(directory, $".{baseName}.trim-{Guid.NewGuid():N}{extension}");
        }

        private static string GetBackupPath(string sourceFilePath)
        {
            string directory = Path.GetDirectoryName(sourceFilePath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(sourceFilePath);
            string extension = Path.GetExtension(sourceFilePath);
            return PathUtils.Combine(directory, $".{baseName}.backup-{Guid.NewGuid():N}{extension}");
        }

        private static void TryDelete(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to delete trim working file {FilePath}", filePath);
            }
        }
    }
}
