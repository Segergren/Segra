using System.Text.Json.Serialization;

namespace Segra.Backend.Core.Models
{
    public class Bookmark
    {
        private static readonly Random random = new Random();
        public int Id { get; set; } = random.Next(1, int.MaxValue);
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public BookmarkType Type { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public BookmarkSubtype? Subtype { get; set; }
        public TimeSpan Time { get; set; }
        // TODO (os): Set this rating from the ai analysis
        public int? AiRating { get; set; }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BookmarkType
    {
        Manual,
        Kill,
        Assist,
        Death
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BookmarkSubtype
    {
        Headshot
    }
}
