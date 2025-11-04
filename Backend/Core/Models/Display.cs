using System.Text.Json.Serialization;

namespace Segra.Backend.Core.Models
{
    public class Display : IEquatable<Display>
    {
        [JsonPropertyName("deviceName")]
        public required string DeviceName { get; set; }

        [JsonPropertyName("deviceId")]
        public required string DeviceId { get; set; }

        [JsonPropertyName("isPrimary")]
        public required bool IsPrimary { get; set; }

        public bool Equals(Display? other)
        {
            if (other == null) return false;

            return DeviceName == other.DeviceName &&
                   DeviceId == other.DeviceId &&
                   IsPrimary == other.IsPrimary;
        }

        public override bool Equals(object? obj)
        {
            if (obj is Display display)
            {
                return Equals(display);
            }
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(DeviceName, DeviceId, IsPrimary);
        }
    }
}
