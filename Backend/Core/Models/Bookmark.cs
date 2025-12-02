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
        [IncludeInHighlight] Kill,
        [IncludeInHighlight] Goal,
        Assist,
        Death
    }

    /// <summary>
    /// Marks a BookmarkType as one that should be included in auto-generated highlights.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class IncludeInHighlightAttribute : Attribute { }

    public static class BookmarkTypeExtensions
    {
        /// <summary>
        /// Returns true if this bookmark type should be included in auto-generated highlights.
        /// </summary>
        public static bool IncludeInHighlight(this BookmarkType type) =>
            typeof(BookmarkType).GetField(type.ToString())!
                .GetCustomAttributes(typeof(IncludeInHighlightAttribute), false).Length > 0;
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BookmarkSubtype
    {
        Headshot
    }
}
