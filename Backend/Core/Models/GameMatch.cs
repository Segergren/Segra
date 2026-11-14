using System.Text.Json.Serialization;

namespace Segra.Backend.Core.Models
{
    // A match inside a video, with times relative to the start of the video
    public class GameMatch
    {
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public MatchResult? Result { get; set; }
        // Rounds won and lost
        public int? Score { get; set; }
        public int? OpponentScore { get; set; }
        public string? Map { get; set; }
        public string? Mode { get; set; }
        // Hero or champion
        public string? Character { get; set; }
        public int? Kills { get; set; }
        public int? Deaths { get; set; }
        public int? Assists { get; set; }

        public GameMatch Clone() => (GameMatch)MemberwiseClone();

        public GameMatch Shifted(TimeSpan offset)
        {
            var copy = Clone();
            copy.StartTime += offset;
            copy.EndTime += offset;
            return copy;
        }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MatchResult
    {
        Win,
        Loss,
        Draw
    }
}
