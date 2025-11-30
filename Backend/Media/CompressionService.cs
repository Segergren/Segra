using Serilog;
using Segra.Backend.Core.Models;
using Segra.Backend.Services;
using Segra.Backend.App;

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
                string directory = Path.GetDirectoryName(filePath)!;
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                string extension = Path.GetExtension(filePath);
                string tempOutputPath = Path.Combine(directory, $"{fileName}_temp_compressed{extension}");

                TimeSpan durationTs = await FFmpegService.GetVideoDuration(filePath);
                double? duration = durationTs.TotalSeconds > 0 ? durationTs.TotalSeconds : null;

                Log.Information($"Starting compression for: {filePath} (Original size: {originalSize / 1024 / 1024}MB)");
                await MessageService.SendFrontendMessage("CompressionProgress", new { filePath, progress = 0, status = "compressing" });

                string arguments = $"-y -i \"{filePath}\" -c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 128k -movflags +faststart \"{tempOutputPath}\"";

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

                Content.ContentType contentType = directory.EndsWith("clips", StringComparison.OrdinalIgnoreCase)
                    ? Content.ContentType.Clip
                    : Content.ContentType.Highlight;

                Content? originalContent = Settings.Instance.State.Content.FirstOrDefault(c => c.FilePath == filePath);
                string? game = originalContent?.Game;

                string finalPath;
                if (Settings.Instance.RemoveOriginalAfterCompression)
                {
                    finalPath = Path.Combine(directory, $"{fileName}_compressed{extension}");
                    if (File.Exists(finalPath)) File.Delete(finalPath);
                    File.Move(tempOutputPath, finalPath);
                    
                    Log.Information($"Replaced original with compressed file: {finalPath}");
                    await ContentService.CreateMetadataFile(finalPath, contentType, game ?? "Unknown", originalContent?.Bookmarks, originalContent?.Title, originalContent?.CreatedAt);
                    await ContentService.CreateThumbnail(finalPath, contentType);
                    await ContentService.CreateWaveformFile(finalPath, contentType);

                    await Task.Delay(500);
                    await ContentService.DeleteContent(filePath, contentType, false);
                }
                else
                {
                    finalPath = Path.Combine(directory, $"{fileName}_compressed{extension}");
                    if (File.Exists(finalPath)) File.Delete(finalPath);
                    File.Move(tempOutputPath, finalPath);
                    Log.Information($"Saved compressed file as: {finalPath}");

                    await ContentService.CreateMetadataFile(finalPath, contentType, game ?? "Unknown");
                    await ContentService.CreateThumbnail(finalPath, contentType);
                    await ContentService.CreateWaveformFile(finalPath, contentType);
                }
                await MessageService.SendFrontendMessage("CompressionProgress", new { filePath, progress = 100, status = "done" });
                await SettingsService.LoadContentFromFolderIntoState();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error compressing video: {filePath}");
                await MessageService.SendFrontendMessage("CompressionProgress", new { filePath, progress = -1, status = "error", message = ex.Message });
            }
        }
    }
}
