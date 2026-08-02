using System.Text.Json.Serialization;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Detection;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventType
{
    Trigger,
    Exclusion
}

public class EventDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public EventType Type { get; set; }
    public int ClassId { get; set; }
    public BookmarkType? BookmarkType { get; set; }
    public float? ScreenRegionX { get; set; }
    public float? ScreenRegionY { get; set; }
    public float? ScreenRegionW { get; set; }
    public float? ScreenRegionH { get; set; }
}

public class DetectionResult
{
    public int ClassId { get; set; }
    public float Confidence { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public DateTime Timestamp { get; set; }
}

internal class RegionGroup
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
}
