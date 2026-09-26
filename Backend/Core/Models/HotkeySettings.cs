using System.Text.Json.Serialization;

namespace Segra.Backend.Core.Models
{
    public class Hotkey
    {
        [JsonPropertyName("action")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public HotkeyAction Action { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("keys")]
        public List<int> Keys { get; set; }

        public Hotkey(List<int> keys, HotkeyAction action, bool enabled = true)
        {
            Keys = keys;
            Action = action;
            Enabled = enabled;
        }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HotkeyAction
    {
        CreateBookmark,
        SaveReplayBuffer,
        ToggleRecording,
        TogglePreview
    }
}
