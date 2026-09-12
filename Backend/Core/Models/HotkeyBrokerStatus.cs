using System.Text.Json.Serialization;

namespace Segra.Backend.Core.Models
{
    /// <summary>
    /// State of the elevated-hotkey helper. <c>ActionRequired</c> means an install or update is
    /// needed and the automatic attempt was declined or failed, so Settings shows a warning card.
    /// </summary>
    public sealed record HotkeyBrokerStatus(
        [property: JsonPropertyName("installed")] bool Installed,
        [property: JsonPropertyName("upToDate")] bool UpToDate,
        [property: JsonPropertyName("connected")] bool Connected,
        [property: JsonPropertyName("actionRequired")] bool ActionRequired);
}
