using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Tests;

internal static class TestSerializationOptions
{
    public static readonly JsonSerializerOptions CamelCaseIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    public static readonly JsonSerializerOptions CamelCaseRead = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    public static readonly JsonSerializerOptions CamelCaseIndentedEnums = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
