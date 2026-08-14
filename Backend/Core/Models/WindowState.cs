using System.Text.Json.Serialization;

namespace Segra.Backend.Core.Models
{
    // Last known main-window position and size, restored on next launch instead of
    // centering on the primary monitor. Backend-only; never surfaced in the settings UI.
    public class WindowState : IEquatable<WindowState>
    {
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        // 0 (e.g. from settings saved by older versions) means no saved size.
        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("maximized")]
        public bool Maximized { get; set; }

        public bool Equals(WindowState? other)
        {
            if (other == null) return false;

            return X == other.X && Y == other.Y &&
                Width == other.Width && Height == other.Height &&
                Maximized == other.Maximized;
        }

        public override bool Equals(object? obj)
        {
            if (obj is WindowState windowState)
            {
                return Equals(windowState);
            }
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Width, Height, Maximized);
        }
    }
}
