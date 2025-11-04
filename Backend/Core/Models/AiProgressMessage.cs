namespace Segra.Backend.Core.Models
{
    public class AiProgressMessage
    {
        public required string Id { get; set; }
        public string? FileName { get; set; }
        public required int Progress { get; set; }
        public required string Status { get; set; }
        public required string Message { get; set; }
        public required Content Content { get; set; }
    }
}
