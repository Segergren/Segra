using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Core
{
    // Source-generated settings.json metadata, so startup doesn't build reflection serializers.
    // Types outside the Settings graph fall back to reflection.
    [JsonSerializable(typeof(Settings))]
    internal partial class SettingsJsonContext : JsonSerializerContext
    {
        public static readonly IJsonTypeInfoResolver Resolver = JsonTypeInfoResolver.Combine(Default, new DefaultJsonTypeInfoResolver());
    }
}
