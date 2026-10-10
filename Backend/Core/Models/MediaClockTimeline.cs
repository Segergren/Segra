using System.Text.Json.Serialization;

namespace Segra.Backend.Core.Models
{
    /// <summary>
    /// One breakpoint of the wall-clock to media-position mapping.
    /// </summary>
    public class MediaClockSample
    {
        [JsonPropertyName("wallSeconds")]
        public double WallSeconds { get; set; }

        [JsonPropertyName("mediaSeconds")]
        public double MediaSeconds { get; set; }
    }

    public class MediaClockTimeline
    {
        [JsonPropertyName("samples")]
        public List<MediaClockSample> Samples { get; set; } = [];

        [JsonIgnore]
        public double LostSeconds => Samples.Count < 2
            ? 0
            : Samples[^1].WallSeconds - Samples[0].WallSeconds
              - (Samples[^1].MediaSeconds - Samples[0].MediaSeconds);

        public double ToMedia(double wallSeconds)
        {
            if (Samples.Count == 0) return wallSeconds;

            if (wallSeconds <= Samples[0].WallSeconds) return Samples[0].MediaSeconds;

            for (int i = 1; i < Samples.Count; i++)
            {
                MediaClockSample b = Samples[i];
                if (wallSeconds > b.WallSeconds) continue;

                MediaClockSample a = Samples[i - 1];
                double span = b.WallSeconds - a.WallSeconds;
                return span <= 0
                    ? b.MediaSeconds
                    : a.MediaSeconds + (wallSeconds - a.WallSeconds) / span * (b.MediaSeconds - a.MediaSeconds);
            }

            return Samples[^1].MediaSeconds;
        }

        public MediaClockTimeline Copy() => new()
        {
            Samples = [.. Samples.Select(s => new MediaClockSample { WallSeconds = s.WallSeconds, MediaSeconds = s.MediaSeconds })]
        };
    }
}
