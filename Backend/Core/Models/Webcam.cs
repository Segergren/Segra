using System.Text.Json.Serialization;

namespace Segra.Backend.Core.Models
{
    public record Webcam(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("deviceId")] string DeviceId);
}
