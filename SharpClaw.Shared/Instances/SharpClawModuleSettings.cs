using System.Text.Json;

namespace SharpClaw.Shared.Instances;

/// <summary>Version 1 of the documented frontend JSON extension to package.json.</summary>
public static class SharpClawModuleSettings
{
    public const int MaximumDocumentBytes = 32 * 1024;
    private static readonly JsonSerializerOptions DocumentJson = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    public static IReadOnlyList<SharpClawModuleSettingsPage> ReadManifest(
        string json, string sourceId, string moduleName)
    {
        // Preserve the existing package manifest parser's comment/depth compatibility;
        // only the optional settings extension is interpreted here.
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip });
        if (!document.RootElement.TryGetProperty("frontend", out var frontend)) return [];
        if (frontend.ValueKind != JsonValueKind.Object ||
            !frontend.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1)
            throw new InvalidDataException("Unsupported module frontend schema version.");
        if (!frontend.TryGetProperty("settings", out var settings)) return [];
        if (settings.ValueKind != JsonValueKind.Array || settings.GetArrayLength() > 16)
            throw new InvalidDataException("A module can declare at most 16 settings pages.");
        var pages = new List<SharpClawModuleSettingsPage>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in settings.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var title = item.GetProperty("title").GetString()!;
            var read = item.GetProperty("readPath").GetString()!;
            var save = item.GetProperty("savePath").GetString()!;
            if (!IsIdentifier(id) || !ids.Add(id) || !IsLabel(title) ||
                !IsEndpointPath(read) || !IsEndpointPath(save))
                throw new InvalidDataException("Invalid or duplicate module settings declaration.");
            pages.Add(new(sourceId, moduleName, id, title, read, save));
        }
        return pages;
    }

    public static SharpClawModuleSettingsDocument ReadDocument(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("Module settings document exceeds its limit.");
        var document = JsonSerializer.Deserialize<SharpClawModuleSettingsDocument>(json, DocumentJson)
            ?? throw new InvalidDataException("Missing module settings document.");
        if (document.SchemaVersion != 1 || document.Fields is null || document.Values is null ||
            document.Fields.Count > 32)
            throw new InvalidDataException("Unsupported module settings document.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in document.Fields)
        {
            if (field is null || !IsIdentifier(field.Key) || !keys.Add(field.Key) || !IsLabel(field.Label) ||
                field.Kind is not ("text" or "secret" or "boolean" or "choice") ||
                (string.Equals(field.Kind, "choice", StringComparison.Ordinal) && (field.Choices is null || field.Choices.Count is 0 or > 64 ||
                    field.Choices.Any(choice => !IsLabel(choice)) ||
                    field.Choices.Distinct(StringComparer.Ordinal).Count() != field.Choices.Count)))
                throw new InvalidDataException("Invalid module settings field.");
            if (document.Values.TryGetValue(field.Key, out var value) && !string.Equals(field.Kind, "secret", StringComparison.Ordinal))
            {
                if (string.Equals(field.Kind, "boolean", StringComparison.Ordinal) ? value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) :
                    value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 4096 ||
                    (string.Equals(field.Kind, "choice", StringComparison.Ordinal) && !field.Choices!.Contains(value.GetString(), StringComparer.Ordinal)))
                    throw new InvalidDataException("Settings value does not match its declared field.");
            }
        }
        if (document.Values.Keys.Any(key => !keys.Contains(key)))
            throw new InvalidDataException("Settings values contain undeclared fields.");
        return document;
    }

    public static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } && value is not ("." or "..") &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    public static bool IsLabel(string? value) => value is { Length: > 0 and <= 256 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    public static bool IsEndpointPath(string? value) => value is { Length: > 1 and <= 512 } &&
        value[0] == '/' && value[1] != '/' &&
        value[1..].Split('/').All(segment => IsIdentifier(segment) && segment is not ("." or ".."));
}
