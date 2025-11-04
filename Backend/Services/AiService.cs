using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;
using Segra.Backend.Utils;
using Segra.Backend.Core.Models;
using Segra.Backend.Media;
using Segra.Backend.App;

namespace Segra.Backend.Services
{
    internal class AiService
    {
        // TODO (os): add a cancel button
        public static async Task AnalyzeVideo(string fileName)
        {
            try
            {
                Log.Information("Starting to analyze video: " + fileName);
                Content? content = Settings.Instance.State.Content.FirstOrDefault(x => x.FileName == fileName);

                if (content == null)
                {
                    Log.Information("No content found matching fileName: " + fileName);
                    return;
                }

                List<Bookmark> bookmarks = content.Bookmarks;

                if (bookmarks.Count == 0)
                {
                    Log.Information("No bookmarks found for video: " + fileName);
                    return;
                }

                var payload = new
                {
                    filename = content.FileName,
                    game = content.Game
                };

                string jsonPayload = JsonSerializer.Serialize(payload);
                using var jsonContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, "https://processing.segra.tv/ai/new")
                {
                    Content = jsonContent
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AuthService.GetJwtAsync());

                using var client = new HttpClient();
                HttpResponseMessage response;
                try
                {
                    response = client.Send(request);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to send the request to the AI service.");
                    return;
                }

                string responseBody;
                try
                {
                    responseBody = response.Content.ReadAsStringAsync().Result.Trim();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to read the AI service response content.");
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning($"AI service returned non-success status code: {response.StatusCode} - Body: {responseBody}");
                    return;
                }

                string? analysisId;
                try
                {
                    var responseJson = JsonDocument.Parse(responseBody);
                    analysisId = responseJson.RootElement.GetProperty("analysis_id").GetString();

                    if (analysisId == null)
                    {
                        Log.Error("Failed to parse 'analysis_id' from the AI service response.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to parse 'analysis_id' from the AI service response.");
                    return;
                }

                Log.Information("Analysis id: " + analysisId);

                AiProgressMessage aiProgressMessage = new AiProgressMessage
                {
                    Id = analysisId,
                    Progress = 0,
                    Status = "processing",
                    Message = "Finding parts to analyze",
                    Content = content
                };

                _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);

                Dictionary<Bookmark, string> clipPaths = new Dictionary<Bookmark, string>();

                decimal increment = 20m / bookmarks.Count;
                decimal currentProgress = 0;
                foreach (var bookmark in bookmarks)
                {
                    Log.Information($"Generating clip for bookmark {bookmark.Id}...");
                    string? clipPath = null;
                    try
                    {
                        clipPath = await ClipService.CreateAiClipToAnalyzeFromBookmark(bookmark, content);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Failed to generate clip for bookmark {bookmark.Id}");
                    }

                    if (!string.IsNullOrEmpty(clipPath))
                    {
                        clipPaths.Add(bookmark, clipPath);
                        Log.Information($"Generated clip to analyze for bookmark {bookmark.Id}: {Path.GetFileName(clipPath)}");
                    }
                    else
                    {
                        Log.Information($"Failed to generate clip to analyze for bookmark {bookmark.Id}");
                    }

                    currentProgress += increment;

                    aiProgressMessage.Progress = (int)Math.Floor(currentProgress);
                    aiProgressMessage.Message = "Extracting metadata to analyze";
                    _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);

                    // Delay a bit before generating the next clip
                    await Task.Delay(1000);
                }

                aiProgressMessage.Progress = 20;
                _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);
                Log.Information($"All {clipPaths.Count} clips have been generated. Starting parallel upload to AI service...");

                decimal uploadRange = 30m;
                increment = clipPaths.Count > 0 ? uploadRange / clipPaths.Count : 0m;
                int completed = 0;

                var uploadTasks = new List<Task>();
                foreach (var item in clipPaths)
                {
                    var bookmark = item.Key;
                    var clipPath = item.Value;

                    uploadTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await AnalyzeAiClipToAnalyzeAsync(content, bookmark, analysisId, clipPath);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, $"Upload failed for bookmark {bookmark.Id}");
                        }

                        var count = Interlocked.Increment(ref completed);
                        var newProgress = 20 + (count * increment);

                        aiProgressMessage.Progress = (int)Math.Floor(newProgress);
                        aiProgressMessage.Message = $"Uploaded {count} of {clipPaths.Count} parts to analyze";
                        _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);
                    }));
                }

                try
                {
                    await Task.WhenAll(uploadTasks);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "One or more uploads failed during parallel processing.");
                }

                aiProgressMessage.Progress = 50;
                aiProgressMessage.Message = $"Analyzing parts...";
                _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);

                Log.Information($"All {clipPaths.Count} clips have been uploaded or attempted.");
                await ProcessAnalysisAsync(aiProgressMessage);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected error in AnalyzeVideo");
            }
        }

        private static async Task ProcessAnalysisAsync(AiProgressMessage aiProgressMessage)
        {
            List<Bookmark> highRatedBookmarks = new List<Bookmark>();

            if (!string.IsNullOrEmpty(aiProgressMessage.Id))
            {
                try
                {
                    string? finalStatusResponse = await WaitForAnalysisCompletionAsync(aiProgressMessage, 20);

                    if (!string.IsNullOrEmpty(finalStatusResponse))
                    {
                        Log.Information($"Final analysis status received: {finalStatusResponse}");
                        // Process the analysis results
                        highRatedBookmarks = ProcessAnalysisResultsAndGetHighRatedBookmarks(finalStatusResponse, aiProgressMessage.Content);
                    }
                    else
                    {
                        Log.Information("Analysis timed out or failed to complete");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Error during analysis monitoring (analysisId: {aiProgressMessage.Id})");
                }
            }
            else
            {
                Log.Information("Cannot check analysis status: missing analysis_id");
            }

            List<Bookmark> highRatedKillsBookmarks = highRatedBookmarks
                .Where(b => b.Type == BookmarkType.Kill)
                .ToList();

            if (highRatedKillsBookmarks.Any())
            {
                Log.Information($"Creating clips for {highRatedKillsBookmarks.Count} high-rated kills bookmarks");
                try
                {
                    await ClipService.CreateAiClipFromBookmarks(highRatedKillsBookmarks, aiProgressMessage);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to create final AI clips from high-rated kills bookmarks.");
                }
            }
            else
            {
                Log.Information("No high-rated kills bookmarks found, no clips will be created");
            }


            aiProgressMessage.Progress = 100;
            aiProgressMessage.Status = "done";
            aiProgressMessage.Message = "Done";
            _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);
        }

        private static async Task<string?> WaitForAnalysisCompletionAsync(AiProgressMessage aiProgressMessage, int maxWaitMinutes = 20)
        {
            aiProgressMessage.Progress = 50;
            aiProgressMessage.Message = $"Waiting for analysis...";
            _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);

            DateTime timeoutTime = DateTime.Now.AddMinutes(maxWaitMinutes);
            await Task.Delay(5000);

            while (DateTime.Now < timeoutTime)
            {
                try
                {
                    var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"https://processing.segra.tv/ai/status/{aiProgressMessage.Id}");
                    statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AuthService.GetJwtAsync());
                    using var statusClient = new HttpClient();
                    var statusResponse = await statusClient.SendAsync(statusRequest);
                    var statusResponseBody = (await statusResponse.Content.ReadAsStringAsync()).Trim();

                    if (!statusResponse.IsSuccessStatusCode)
                    {
                        Log.Information($"Failed to check analysis status. Status: {statusResponse.StatusCode}, Response: {statusResponseBody}");
                        await Task.Delay(15000);
                        continue;
                    }

                    var statusJson = JsonDocument.Parse(statusResponseBody);
                    var status = statusJson.RootElement.GetProperty("status").GetString();
                    Log.Information($"Current analysis status: {status}");

                    // If done or error, finalize progress at 80 and return
                    if (status == "completed")
                    {
                        aiProgressMessage.Progress = 80;
                        aiProgressMessage.Message = $"Creating highlight";
                        _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);

                        return statusResponseBody;
                    }
                    else if (status == "error")
                    {
                        aiProgressMessage.Progress = 80;
                        aiProgressMessage.Message = $"Analysis error";
                        _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);
                        return statusResponseBody;
                    }

                    // Not completed yet – see how many clips are done
                    int totalClips = 0;
                    int completedClips = 0;
                    if (statusJson.RootElement.TryGetProperty("clips", out var clipsElement) &&
                        clipsElement.ValueKind == JsonValueKind.Array)
                    {
                        var clips = clipsElement.EnumerateArray().ToList();
                        totalClips = clips.Count;
                        completedClips = clips.Count(c => c.GetProperty("status").GetString() == "completed");
                    }

                    decimal newProgress = 50m;
                    if (totalClips > 0)
                        newProgress += (completedClips / (decimal)totalClips) * 30m;

                    // Keep it under 80 until they're all done
                    if (newProgress > 79m) newProgress = 79m;

                    aiProgressMessage.Progress = (int)Math.Floor(newProgress);
                    aiProgressMessage.Message = completedClips == 0 ? "Finding highlights..." : $"Finding highlights... {completedClips} found so far.";
                    _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);

                    await Task.Delay(5000);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error checking analysis status.");
                    await Task.Delay(15000);
                }
            }

            // If we exit the loop, we timed out
            Log.Information($"Timed out waiting for analysis to complete after {maxWaitMinutes} minutes");

            aiProgressMessage.Progress = 80;
            aiProgressMessage.Message = "Analysis timed out";
            _ = MessageService.SendFrontendMessage("AiProgress", aiProgressMessage);

            return null;
        }

        private static List<Bookmark> ProcessAnalysisResultsAndGetHighRatedBookmarks(string statusResponseBody, Content content)
        {
            List<Bookmark> highRatedBookmarks = new List<Bookmark>();

            try
            {
                var statusJson = JsonDocument.Parse(statusResponseBody);
                var status = statusJson.RootElement.GetProperty("status").GetString();

                if (status == "completed")
                {
                    if (statusJson.RootElement.TryGetProperty("clips", out var clipsElement) &&
                        clipsElement.ValueKind == JsonValueKind.Array)
                    {
                        var clips = clipsElement.EnumerateArray().ToList();
                        Log.Information($"Received {clips.Count} analyzed clips");

                        foreach (var clip in clips)
                        {
                            string bookmarkId = clip.GetProperty("bookmark_id").GetString() ?? string.Empty;
                            string clipStatus = clip.GetProperty("status").GetString() ?? string.Empty;

                            if (bookmarkId == "" || clipStatus == "") continue;

                            if (clipStatus == "completed")
                            {
                                int rating = clip.GetProperty("rating").GetInt32();
                                string summary = clip.GetProperty("summary").GetString() ?? string.Empty;

                                Log.Information($"Clip {bookmarkId}: Rating={rating}, Summary={summary}");

                                var bookmark = content.Bookmarks.FirstOrDefault(b => b.Id.ToString() == bookmarkId);
                                if (bookmark != null)
                                {
                                    if (rating >= 6)
                                    {
                                        Log.Information($"Adding bookmark {bookmark.Id} to high-rated list with rating {rating}");
                                        highRatedBookmarks.Add(bookmark);
                                    }
                                }
                                else
                                {
                                    Log.Information($"No matching bookmark found for bookmark id {bookmarkId}");
                                }
                            }
                            else if (clipStatus == "error")
                            {
                                string error = clip.GetProperty("error").GetString() ?? string.Empty;
                                Log.Information($"Clip {bookmarkId} analysis failed: {error}");
                            }
                            else
                            {
                                Log.Information($"Clip {bookmarkId} has status: {clipStatus}");
                            }
                        }
                    }
                    else
                    {
                        Log.Information("No clips found in analysis response");
                    }
                }
                else
                {
                    Log.Information($"Analysis status: {status}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing analysis results.");
            }

            return highRatedBookmarks.OrderBy(b => b.Time).ToList();
        }

        private static async Task AnalyzeAiClipToAnalyzeAsync(Content content, Bookmark bookmark, string analysisId, string clipPath)
        {
            try
            {
                if (string.IsNullOrEmpty(clipPath) || !File.Exists(clipPath))
                {
                    Log.Information($"Clip file not found or invalid for bookmark {bookmark.Id}");
                    return;
                }

                Log.Information($"Processing ai clip to analyze: {Path.GetFileName(clipPath)} for {bookmark.Type} bookmark");

                using var client = new HttpClient();
                using var formContent = new MultipartFormDataContent();

                var fileBytes = await File.ReadAllBytesAsync(clipPath);
                var fileContent = new ByteArrayContent(fileBytes);
                formContent.Add(fileContent, "file", Path.GetFileName(clipPath));

                formContent.Add(new StringContent(analysisId), "analysis_id");
                formContent.Add(new StringContent(bookmark.Id.ToString()), "bookmark_id");

                var request = new HttpRequestMessage(HttpMethod.Post, "https://processing.segra.tv/ai/add")
                {
                    Content = formContent
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AuthService.GetJwtAsync());

                var response = await client.SendAsync(request);
                var uploadResponseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    Log.Information($"Successfully uploaded clip for bookmark {bookmark.Id}. Response: {uploadResponseBody.Trim()}");
                }
                else
                {
                    Log.Information($"Failed to upload clip for bookmark {bookmark.Id}. Status: {response.StatusCode}, Response: {uploadResponseBody.Trim()}");
                }

                if (File.Exists(clipPath))
                {
                    File.Delete(clipPath);
                }
            }
            catch (Exception ex)
            {
                Log.Information($"Error processing clip for bookmark {bookmark.Id}: {ex.Message}");
                if (!string.IsNullOrEmpty(clipPath) && File.Exists(clipPath))
                {
                    File.Delete(clipPath);
                }
            }
        }
    }
}
