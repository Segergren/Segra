#if ENABLE_TRAINING_EVENTS

using System.Text.Json.Serialization;

namespace Segra.Backend.Training;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventType
{
    Trigger,
    Exclusion
}

public class TrainingEventDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public EventType Type { get; set; }
    public int ClassId { get; set; }
    public float? ScreenRegionX { get; set; }
    public float? ScreenRegionY { get; set; }
    public float? ScreenRegionW { get; set; }
    public float? ScreenRegionH { get; set; }
}

public class TrainingSample
{
    public string ImagePath { get; set; } = string.Empty;
    public string LabelPath { get; set; } = string.Empty;
    public int EventId { get; set; }
    public float BoxX { get; set; } = 0.5f;
    public float BoxY { get; set; } = 0.5f;
    public float BoxW { get; set; } = 1.0f;
    public float BoxH { get; set; } = 1.0f;
    public TimeSpan Timestamp { get; set; }
    public string RecordingFile { get; set; } = string.Empty;
}

public class TrainingEventDetectionResult
{
    public int EventId { get; set; }
    public int ClassId { get; set; }
    public float Confidence { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public DateTime Timestamp { get; set; }
}
#endif
