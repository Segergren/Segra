using System.Text.Json.Serialization;

namespace Segra.Backend.Core.Models
{
    public class Keybind
    {
        [JsonPropertyName("action")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public KeybindAction Action { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("keys")]
        public List<int> Keys { get; set; }

        public Keybind(List<int> keys, KeybindAction action, bool enabled = true)
        {
            Keys = keys;
            Action = action;
            Enabled = enabled;
        }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum KeybindAction
    {
        CreateBookmark,
        SaveReplayBuffer
    }
}
