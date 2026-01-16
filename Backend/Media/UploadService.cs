using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Services;
using Segra.Backend.Shared;
using Serilog;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Segra.Backend.Media
{
    internal static class UploadService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        public static async Task HandleUploadContent(JsonElement message)
        {
            try
            {
                string filePath = message.GetProperty("FilePath").GetString()!;
                string fileName = Path.GetFileName(filePath);
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                string title = message.GetProperty("Title").GetString()!;

                byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
                using var formData = new MultipartFormDataContent();

                int lastSentProgress = 0;
                void ProgressHandler(long sent, long total)
                {
                    if (total <= 0) return;
                    int progress = (int)(sent / (double)total * 100);
                    if (progress >= 100) return;

                    if (progress % 10 == 0 && progress != lastSentProgress)
                    {
                        lastSentProgress = progress;
                        _ = MessageService.SendFrontendMessage("UploadProgress", new
                        {
                            title,
                            fileName,
                            progress,
                            status = "uploading",
                            message = $"Uploading... {progress}%"
                        });
                    }
                }

                var fileContent = new ProgressableStreamContent(fileBytes, "application/octet-stream", ProgressHandler);
                formData.Add(fileContent, "file", fileName);

                AddOptionalContent(formData, message, "Game");
                AddOptionalContent(formData, message, "Title");
                AddOptionalContent(formData, message, "Description");
                AddOptionalContent(formData, message, "IgdbId");

                await MessageService.SendFrontendMessage("UploadProgress", new
                {
                    title,
                    fileName,
                    progress = 0,
                    status = "uploading",
                    message = "Starting upload..."
                });

                var request = new HttpRequestMessage(HttpMethod.Post, "https://processing.segra.tv/upload")
                {
                    Content = formData
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AuthService.GetJwtAsync());

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                await MessageService.SendFrontendMessage("UploadProgress", new
                {
                    title,
                    fileName,
                    progress = 100,
                    status = "done",
                    message = "Upload completed successfully"
                });

                var responseContent = await response.Content.ReadAsStringAsync();
                Log.Information($"Upload success: {responseContent}");

                // Parse the response to extract the URL and update the content with uploadId
                if (!string.IsNullOrEmpty(responseContent))
                {
                    try
                    {
                        var responseJson = JsonSerializer.Deserialize<JsonElement>(responseContent);
                        if (responseJson.TryGetProperty("success", out var successElement) &&
                            successElement.GetBoolean() &&
                            responseJson.TryGetProperty("url", out var urlElement))
                        {
                            string url = urlElement.GetString()!;
                            if (!string.IsNullOrEmpty(url))
                            {
                                // Extract uploadId from the URL (after the last slash)
                                string uploadId = url.Split('/').Last();
                                Log.Information($"Extracted upload ID: {uploadId}");

                                // Update the content with the uploadId
                                var contentList = Settings.Instance.State.Content.ToList();
                                Log.Information($"File name: {fileName}, without extension: {fileNameWithoutExtension}");

                                var contentToUpdate = contentList.FirstOrDefault(c =>
                                    Path.GetFileNameWithoutExtension(c.FileName) == fileNameWithoutExtension);
                                Log.Information($"Content to update: {contentToUpdate?.FileName ?? "not found"}");

                                if (contentToUpdate != null)
                                {
                                    contentToUpdate.UploadId = uploadId;

                                    // Also update the metadata file
                                    string metadataFolderPath = FolderNames.GetMetadataFolderPath(contentToUpdate.Type);
                                    string metadataFilePath = Path.Combine(metadataFolderPath, $"{fileNameWithoutExtension}.json");

                                    var updatedContent = await ContentService.UpdateMetadataFile(metadataFilePath, content =>
                                    {
                                        content.UploadId = uploadId;
                                    });

                                    if (updatedContent != null)
                                    {
                                        Log.Information($"Updated metadata file with upload ID: {metadataFilePath}");
                                    }

                                    Log.Information($"Updated content with upload ID: {uploadId}");
                                    await SettingsService.LoadContentFromFolderIntoState(true);
                                }

                                // Open browser if setting is enabled
                                if (Settings.Instance.ClipShowInBrowserAfterUpload)
                                {
                                    Log.Information($"Opening URL in browser: {url}");
                                    Process.Start(new ProcessStartInfo
                                    {
                                        FileName = url,
                                        UseShellExecute = true
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Failed to parse upload response or update content: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Upload failed: {ex.Message}");
                string errorFileName = message.GetProperty("FilePath").GetString()!;
                string errorTitle = message.GetProperty("Title").GetString()!;

                await MessageService.ShowModal(
                    "Upload Error",
                    "The upload failed.\n" + ex.Message,
                    "error",
                    "Could not upload clip"
                );

                await MessageService.SendFrontendMessage("UploadProgress", new
                {
                    title = errorTitle,
                    fileName = Path.GetFileName(errorFileName),
                    progress = 0,
                    status = "error",
                    message = ex.Message
                });
            }
        }

        private static void AddOptionalContent(MultipartFormDataContent formData, JsonElement message, string field)
        {
            if (message.TryGetProperty(field, out JsonElement element))
            {
                formData.Add(new StringContent(element.GetString()!), field.ToLower());
            }
        }

        public class ProgressableStreamContent : HttpContent
        {
            private readonly byte[] _content;
            private readonly Action<long, long> _progressCallback;

            public ProgressableStreamContent(byte[] content, string mediaType, Action<long, long> progressCallback)
            {
                _content = content ?? throw new ArgumentNullException(nameof(content));
                _progressCallback = progressCallback;
                Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                long totalBytes = _content.Length;
                long totalWritten = 0;
                int bufferSize = 4096;

                for (int i = 0; i < _content.Length; i += bufferSize)
                {
                    int toWrite = Math.Min(bufferSize, _content.Length - i);
                    await stream.WriteAsync(_content.AsMemory(i, toWrite));
                    totalWritten += toWrite;
                    _progressCallback?.Invoke(totalWritten, totalBytes);
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _content.Length;
                return true;
            }
        }

    }
}
