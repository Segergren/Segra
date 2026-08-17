using Serilog;
using Segra.Backend.App;
using Segra.Backend.Core;
using Segra.Backend.Platform;
using Segra.Backend.Shared;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Media
{
    internal static class CompressionService
    {
        // Compresses the video to a temp file capped at maxSizeMb and puts it on the clipboard.
        // The original file is never touched.
        public static async Task CopyCompressedToClipboard(string filePath, int maxSizeMb)
        {
            int processId = Guid.NewGuid().GetHashCode();

            try
            {
                if (!File.Exists(filePath))
                {
                    Log.Error($"File not found for clipboard compression: {filePath}");
                    await MessageService.SendFrontendMessage("ClipboardCompressionProgress", new { filePath, progress = -1, status = "error", message = "File not found" });
                    return;
                }

                long maxBytes = (long)maxSizeMb * 1024 * 1024;
                if (new FileInfo(filePath).Length <= maxBytes)
                {
                    Log.Information($"File already under {maxSizeMb}MB, copying original to clipboard: {filePath}");
                    PlatformServices.Dialogs.CopyFileToClipboard(filePath);
                    await MessageService.SendFrontendMessage("ClipboardCompressionProgress", new { filePath, progress = 100, status = "done" });
                    return;
                }

                string clipboardDir = PathUtils.Combine(FolderNames.CacheFolder, "clipboard");
                Directory.CreateDirectory(clipboardDir);
                foreach (string oldFile in Directory.GetFiles(clipboardDir))
                {
                    try { File.Delete(oldFile); } catch { }
                }

                TimeSpan durationTs = await FFmpegService.GetVideoDuration(filePath);
                double duration = durationTs.TotalSeconds;
                if (duration <= 0)
                {
                    Log.Error($"Could not determine duration for clipboard compression: {filePath}");
                    await MessageService.SendFrontendMessage("ClipboardCompressionProgress", new { filePath, progress = -1, status = "error", message = "Could not read video duration" });
                    return;
                }

                string outputPath = PathUtils.Combine(clipboardDir, $"{Path.GetFileNameWithoutExtension(filePath)}.mp4");
                await MessageService.SendFrontendMessage("ClipboardCompressionProgress", new { filePath, progress = 0, status = "compressing" });
                Log.Information($"Compressing for clipboard (max {maxSizeMb}MB): {filePath}");

                // Aim below the cap so container overhead doesn't push it over, retry lower on overshoot
                double targetBytes = maxBytes * 0.95;
                int audioKbps = 128;
                int videoKbps = (int)(targetBytes * 8 / duration / 1000) - audioKbps;
                if (videoKbps < 300)
                {
                    audioKbps = 64;
                    videoKbps = (int)(targetBytes * 8 / duration / 1000) - audioKbps;
                }
                videoKbps = Math.Max(100, videoKbps);

                bool success = false;
                for (int attempt = 1; attempt <= 3 && !success; attempt++)
                {
                    int maxHeight = videoKbps < 500 ? 480 : videoKbps < 1500 ? 720 : 1080;
                    string arguments = $"-y -i \"{filePath}\" -map 0:v:0 -map 0:a:0? " +
                        $"-vf \"scale=-2:'min({maxHeight},ih)'\" " +
                        $"-c:v libx264 -preset veryfast -b:v {videoKbps}k -maxrate {videoKbps}k -bufsize {videoKbps * 2}k " +
                        $"-c:a aac -b:a {audioKbps}k -movflags +faststart \"{outputPath}\"";

                    await FFmpegService.RunWithProgress(processId, arguments, duration, (progress) =>
                    {
                        _ = MessageService.SendFrontendMessage("ClipboardCompressionProgress", new { filePath, progress = (int)(progress * 100), status = "compressing" });
                    });

                    if (!File.Exists(outputPath))
                    {
                        Log.Error($"Clipboard compression failed for: {filePath}");
                        await MessageService.SendFrontendMessage("ClipboardCompressionProgress", new { filePath, progress = -1, status = "error", message = "Compression failed" });
                        return;
                    }

                    long actualBytes = new FileInfo(outputPath).Length;
                    Log.Information($"Clipboard compression attempt {attempt}: {actualBytes / 1024 / 1024.0:F1}MB (max {maxSizeMb}MB) at {videoKbps}kbps");

                    if (actualBytes <= maxBytes)
                    {
                        success = true;
                    }
                    else
                    {
                        videoKbps = Math.Max(100, (int)(videoKbps * ((double)maxBytes / actualBytes) * 0.9));
                    }
                }

                if (!success)
                {
                    try { File.Delete(outputPath); } catch { }
                    await MessageService.SendFrontendMessage("ClipboardCompressionProgress", new { filePath, progress = -1, status = "error", message = $"Could not compress under {maxSizeMb}MB" });
                    return;
                }

                PlatformServices.Dialogs.CopyFileToClipboard(outputPath);
                await MessageService.SendFrontendMessage("ClipboardCompressionProgress", new { filePath, progress = 100, status = "done" });
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error compressing video for clipboard: {filePath}");
                await MessageService.SendFrontendMessage("ClipboardCompressionProgress", new { filePath, progress = -1, status = "error", message = ex.Message });
            }
        }

        public static async Task CompressVideo(Content originalContent)
        {
            int processId = Guid.NewGuid().GetHashCode();
            string filePath = originalContent.FilePath;

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
                    await ContentService.DeleteContent(filePath, contentType, originalContent.Id, false);
                }

                string finalPath = GetAvailablePath(directory, fileName, extension);
                File.Move(tempOutputPath, finalPath);
                Log.Information(removeOriginal
                    ? $"Replaced original with compressed file: {finalPath}"
                    : $"Saved compressed file as: {finalPath}");

                string? compressedId = await ContentService.CreateMetadataFile(finalPath, contentType, game ?? "Unknown", originalContent.Bookmarks, originalContent.Title, originalContent.CreatedAt, originalContent.IgdbId, originalContent.IsImported, audioTrackNames, originalContent.AudioTrackTypes, compressed: true, gameExePath: originalContent.GameExePath);
                await ContentService.CreateThumbnail(finalPath, contentType, compressedId);
                await ContentService.CreateWaveformFile(finalPath, contentType, compressedId);

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
