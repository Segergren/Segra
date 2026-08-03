using Serilog;
using Segra.Backend.App;
using Segra.Backend.Core;
using Segra.Backend.Shared;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Media
{
    internal static class CompressionService
    {
        public static async Task CompressVideo(string filePath)
        {
            int processId = Guid.NewGuid().GetHashCode();

            try
            {
                if (!File.Exists(filePath))
                {
                    Log.Error($"File not found for compression: {filePath}");
                    return;
                }

                long originalSize = new FileInfo(filePath).Length;
                string directory = PathUtils.Normalize(Path.GetDirectoryName(filePath)!);
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                string extension = Path.GetExtension(filePath);
                string tempOutputPath = PathUtils.Combine(directory, $"{fileName}_temp_compressed{extension}");

                Content? originalContent = AppState.Instance.Content.FirstOrDefault(c => c.FilePath == filePath);
                if (originalContent == null)
                {
                    Log.Error($"Content not found in metadata for file: {filePath}");
                    await MessageService.SendFrontendMessage("CompressionProgress", new { filePath, progress = -1, status = "error", message = "Content not found in metadata" });
                    return;
                }

                TimeSpan durationTs = await FFmpegService.GetVideoDuration(filePath);
                double? duration = durationTs.TotalSeconds > 0 ? durationTs.TotalSeconds : null;

                Log.Information($"Starting compression for: {filePath} (Original size: {originalSize / 1024 / 1024}MB)");
                await MessageService.SendFrontendMessage("CompressionProgress", new { filePath, progress = 0, status = "compressing" });

                List<string>? audioTrackNames = originalContent.AudioTrackNames;
                string trackTitleArgs = audioTrackNames != null
                    ? string.Join(" ", audioTrackNames.Select((name, i) => $"-metadata:s:a:{i} title=\"{name}\"")) + " "
                    : string.Empty;

                string arguments = $"-y -i \"{filePath}\" -map 0:v:0 -map 0:a? -c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 128k {trackTitleArgs}-movflags +faststart \"{tempOutputPath}\"";

                await FFmpegService.RunWithProgress(processId, arguments, duration, (progress) =>
                {
                    _ = MessageService.SendFrontendMessage("CompressionProgress", new { filePath, progress = (int)(progress * 100), status = "compressing" });
                });

                if (!File.Exists(tempOutputPath))
                {
                    Log.Error($"Compression failed for: {filePath}");
                    await MessageService.SendFrontendMessage("CompressionProgress", new { filePath, progress = -1, status = "error", message = "Compression failed" });
                    return;
                }

                long compressedSize = new FileInfo(tempOutputPath).Length;
                Log.Information($"Compression complete. Original: {originalSize / 1024 / 1024}MB, Compressed: {compressedSize / 1024 / 1024}MB");

                if (compressedSize >= originalSize)
                {
                    Log.Information($"Compressed file is not smaller than original, keeping original");
                    File.Delete(tempOutputPath);
                    await MessageService.SendFrontendMessage("CompressionProgress", new { filePath, progress = 100, status = "skipped", message = "Compressed file was not smaller" });
                    return;
                }

                Content.ContentType contentType = originalContent.Type;
                string? game = originalContent.Game;

                bool removeOriginal = Settings.Instance.RemoveOriginalAfterCompression;
                if (removeOriginal)
                {
                    // Delete first so the compressed file can take over the original name and its
                    // thumbnail/waveform/metadata aren't removed right after being written.
                    await Task.Delay(500);
                    await ContentService.DeleteContent(filePath, contentType, false);
                }

                string finalPath = GetAvailablePath(directory, fileName, extension);
                File.Move(tempOutputPath, finalPath);
                Log.Information(removeOriginal
                    ? $"Replaced original with compressed file: {finalPath}"
                    : $"Saved compressed file as: {finalPath}");

                await ContentService.CreateMetadataFile(finalPath, contentType, game ?? "Unknown", originalContent.Bookmarks, originalContent.Title, originalContent.CreatedAt, originalContent.IgdbId, originalContent.IsImported, audioTrackNames, compressed: true);
                await ContentService.CreateThumbnail(finalPath, contentType);
                await ContentService.CreateWaveformFile(finalPath, contentType);

                await MessageService.SendFrontendMessage("CompressionProgress", new { filePath, progress = 100, status = "done" });
                await SettingsService.LoadContentFromFolderIntoState();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error compressing video: {filePath}");
                await MessageService.SendFrontendMessage("CompressionProgress", new { filePath, progress = -1, status = "error", message = ex.Message });
            }
        }

        // Keeps the original name when it's free, otherwise appends " (1)", " (2)", ...
        private static string GetAvailablePath(string directory, string fileName, string extension)
        {
            string candidate = PathUtils.Combine(directory, $"{fileName}{extension}");
            int counter = 1;
            while (File.Exists(candidate))
            {
                candidate = PathUtils.Combine(directory, $"{fileName} ({counter}){extension}");
                counter++;
            }
            return candidate;
        }
    }
}
