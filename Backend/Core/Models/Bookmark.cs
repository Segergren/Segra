using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Segra.Backend.Core.Models
{
    public class Bookmark
    {
        public int Id { get; set; } = Random.Shared.Next(1, int.MaxValue);
        [JsonConverter(typeof(BookmarkTypeConverter))]
        public BookmarkType Type { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public BookmarkSubtype? Subtype { get; set; }
        public TimeSpan Time { get; set; }
        // TODO (os): Set this rating from the ai analysis
        public int? AiRating { get; set; }
    }

    /// <summary>
    /// Converts BookmarkType from JSON, falling back to Manual for unknown/removed values.
    /// </summary>
    internal sealed class BookmarkTypeConverter : JsonConverter<BookmarkType>
    {
        public override BookmarkType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value != null && Enum.TryParse<BookmarkType>(value, ignoreCase: true, out var result))
                return result;
            Log.Warning($"Unknown bookmark type '{value}' in JSON, defaulting to Manual");
            return BookmarkType.Manual;
        }

        public override void Write(Utf8JsonWriter writer, BookmarkType value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
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
