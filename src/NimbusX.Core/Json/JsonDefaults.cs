using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NimbusX.Core.Json;

/// <summary>
/// One serialiser configuration for every NimbusX file, so formats stay stable between app versions.
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Compact options for line oriented protocols (telemetry, JSON lines).</summary>
    public static readonly JsonSerializerOptions Lines = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static bool TryDeserialize<T>(string json, out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}
