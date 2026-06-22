using Serilog;
using Segra.Backend.App;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Media
{
    internal class AiService
    {
        public static async Task CreateHighlight(string fileName)
        {
            string highlightId = Guid.NewGuid().ToString();
            Content? content = null;

            try
            {
                Log.Information($"Starting highlight creation for: {fileName}");

                content = AppState.Instance.Content.FirstOrDefault(x => x.FileName == fileName);
                if (content == null)
                {
                    Log.Warning($"No content found matching fileName: {fileName}");
                    return;
                }

                int momentCount = content.Bookmarks.Count(b => b.Type.IncludeInHighlight());
                if (momentCount == 0)
                {
                    Log.Information($"No highlight bookmarks found for: {fileName}");
                    await SendProgress(highlightId, -1, "error", "No highlight moments found in this session", content, "AiProgress");
                    return;
                }

                await SendProgress(highlightId, 0, "processing", $"Found {momentCount} moments", content, "AiProgress");

                await HighlightService.CreateHighlightFromBookmarks(fileName, async (progress, message) =>
                {
                    string status = progress < 0 ? "error" : progress >= 100 ? "done" : "processing";
                    await SendProgress(highlightId, progress, status, message, content, "AiProgress");
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error creating highlight for {fileName}");
                if (content != null)
                {
                    await SendProgress(highlightId, -1, "error", $"Error: {ex.Message}", content, "AiProgress");
                }
            }
        }

        public static async Task CreateLowlight(string fileName)
        {
            string lowlightId = Guid.NewGuid().ToString();
            Content? content = null;

            try
            {
                Log.Information($"Starting lowlight creation for: {fileName}");

                content = AppState.Instance.Content.FirstOrDefault(x => x.FileName == fileName);
                if (content == null)
                {
                    Log.Warning($"No content found matching fileName: {fileName}");
                    return;
                }

                int momentCount = content.Bookmarks.Count(b => b.Type.IncludeInLowlight());
                if (momentCount == 0)
                {
                    Log.Information($"No lowlight bookmarks found for: {fileName}");
                    await SendProgress(lowlightId, -1, "error", "No lowlight moments found in this session", content, "LowlightAiProgress");
                    return;
                }

                await SendProgress(lowlightId, 0, "processing", $"Found {momentCount} moments", content, "LowlightAiProgress");

                await LowlightService.CreateLowlightFromBookmarks(fileName, async (progress, message) =>
                {
                    string status = progress < 0 ? "error" : progress >= 100 ? "done" : "processing";
                    await SendProgress(lowlightId, progress, status, message, content, "LowlightAiProgress");
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error creating lowlight for {fileName}");
                if (content != null)
                {
                    await SendProgress(lowlightId, -1, "error", $"Error: {ex.Message}", content, "LowlightAiProgress");
                }
            }
        }

        private static async Task SendProgress(string id, int progress, string status, string message, Content content, string messageType)
        {
            var progressMessage = new HighlightProgressMessage
            {
                Id = id,
                Progress = progress,
                Status = status,
                Message = message,
                Content = content,
                MessageType = messageType
            };

            await MessageService.SendFrontendMessage(messageType, progressMessage);
        }
    }

    public class HighlightProgressMessage
    {
        public required string Id { get; set; }
        public required int Progress { get; set; }
        public required string Status { get; set; }
        public required string Message { get; set; }
        public required Content Content { get; set; }
        public required string MessageType { get; set; }
    }
}
