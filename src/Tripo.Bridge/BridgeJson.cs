using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tripo.Bridge;

public static class BridgeJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);
}
