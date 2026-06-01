namespace Segra.Backend.Core.Models
{
    public class Segment
    {
        public long Id { get; set; }
        // TODO (os): make this of type ContentType
        public required string Type { get; set; }
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public required string FileName { get; set; }
        public string? FilePath { get; set; }
        public required string Game { get; set; }
        public string Title { get; set; } = string.Empty;
        public int? IgdbId { get; set; }
        public List<int>? MutedAudioTracks { get; set; }
        public Dictionary<int, double>? AudioTrackVolumes { get; set; }
    }
}
