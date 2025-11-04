using System.Text.Json.Serialization;

namespace Segra.Backend.Core.Models
{
    public class Codec : IEquatable<Codec>
    {
        [JsonPropertyName("friendlyName")]
        public required string FriendlyName { get; set; }

        [JsonPropertyName("internalEncoderId")]
        public required string InternalEncoderId { get; set; }

        [JsonPropertyName("isHardwareEncoder")]
        public required bool IsHardwareEncoder { get; set; }

        public bool Equals(Codec? other)
        {
            if (other == null) return false;

            return FriendlyName == other.FriendlyName &&
                   InternalEncoderId == other.InternalEncoderId &&
                   IsHardwareEncoder == other.IsHardwareEncoder;
        }

        public override bool Equals(object? obj)
        {
            if (obj is Codec codec)
            {
                return Equals(codec);
            }
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(FriendlyName, InternalEncoderId, IsHardwareEncoder);
        }
    }
}
