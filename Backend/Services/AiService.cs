using Serilog;
using Segra.Backend.Core.Models;
using Segra.Backend.Media;
using Segra.Backend.App;

namespace Segra.Backend.Services
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

                content = Settings.Instance.State.Content.FirstOrDefault(x => x.FileName == fileName);
                if (content == null)
                {
                    Log.Warning($"No content found matching fileName: {fileName}");
                    return;
                }

                int momentCount = content.Bookmarks.Count(b => b.Type.IncludeInHighlight());
                if (momentCount == 0)
                {
                    Log.Information($"No highlight bookmarks found for: {fileName}");
                    await SendProgress(highlightId, -1, "error", "No highlight moments found in this session", content);
                    return;
                }

                await SendProgress(highlightId, 0, "processing", $"Found {momentCount} moments", content);

                await HighlightService.CreateHighlightFromBookmarks(fileName, async (progress, message) =>
                {
                    string status = progress < 0 ? "error" : progress >= 100 ? "done" : "processing";
                    await SendProgress(highlightId, progress, status, message, content);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error creating highlight for {fileName}");
                if (content != null)
                {
                    await SendProgress(highlightId, -1, "error", $"Error: {ex.Message}", content);
                }
            }
        }

        private static async Task SendProgress(string id, int progress, string status, string message, Content content)
        {
            var progressMessage = new HighlightProgressMessage
            {
                Id = id,
                Progress = progress,
                Status = status,
                Message = message,
                Content = content
            };

            await MessageService.SendFrontendMessage("AiProgress", progressMessage);
        }
    }

    public class HighlightProgressMessage
    {
        public required string Id { get; set; }
        public required int Progress { get; set; }
        public required string Status { get; set; }
        public required string Message { get; set; }
        public required Content Content { get; set; }
    }
}
