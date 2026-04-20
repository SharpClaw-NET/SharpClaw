using System.Buffers;
using System.Text.Json;

namespace SharpClaw.Infrastructure.Persistence.JSON;

/// <summary>
/// Injects a <c>$schemaVersion</c> field into serialized entity JSON objects.
/// <para>
/// This is the RGAP-9 groundwork: every entity file on disk carries a version
/// number so a future <c>IEntityMigrator&lt;T&gt;</c> pipeline can detect and
/// upgrade files written by older code. No migration logic runs yet — reads
/// simply ignore the unknown field (default <see cref="System.Text.Json"/> behavior).
/// </para>
/// </summary>
internal static class JsonSchemaVersion
{
    /// <summary>Current schema version stamped into every entity file.</summary>
    internal const int Current = 1;

    // Pre-encoded prefix: `{\n  "$schemaVersion": 1,\n  ` + rest of object properties.
    // Built once; reused for every serialization.
    private static readonly byte[] s_prefix = BuildPrefix();

    private static byte[] BuildPrefix()
    {
        var buf = new ArrayBufferWriter<byte>(64);
        using var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = true });
        // Write a temporary object just to get the opening brace + field.
        // We'll splice manually, so we only need the bytes up to and including the comma.
        // Output:  {\n  "$schemaVersion": 1,\n
        w.WriteStartObject();
        w.WriteNumber("$schemaVersion", Current);
        w.Flush();
        // buf now contains:  {\n  "$schemaVersion": 1
        // Append  ,\n  so the next property slots in cleanly.
        return [.. buf.WrittenSpan, (byte)',', (byte)'\n', (byte)' ', (byte)' '];
    }

    /// <summary>
    /// Returns a new byte array that is the entity JSON with <c>$schemaVersion</c>
    /// injected as the first field of the root object.
    /// </summary>
    /// <param name="entityJson">Well-formed JSON object bytes from <c>JsonSerializer</c>.</param>
    internal static byte[] Inject(ReadOnlySpan<byte> entityJson)
    {
        // entityJson starts with `{` (possibly with leading whitespace from indented output).
        // Find the first `{`.
        var openBrace = entityJson.IndexOf((byte)'{');
        if (openBrace < 0)
        {
            // Not an object (shouldn't happen for entity types) — return as-is.
            return entityJson.ToArray();
        }

        var afterBrace = entityJson[(openBrace + 1)..];

        // If the object is empty `{}`, just write  { "$schemaVersion": 1 }
        var firstNonWhitespace = afterBrace.TrimStart(" \t\r\n"u8);
        if (firstNonWhitespace.Length > 0 && firstNonWhitespace[0] == (byte)'}')
        {
            var tmp = new ArrayBufferWriter<byte>(64);
            using var tw = new Utf8JsonWriter(tmp, new JsonWriterOptions { Indented = true });
            tw.WriteStartObject();
            tw.WriteNumber("$schemaVersion", Current);
            tw.WriteEndObject();
            tw.Flush();
            return tmp.WrittenSpan.ToArray();
        }

        // Normal case: splice prefix after `{` and keep everything after the opening brace.
        // prefix already contains `{\n  "$schemaVersion": 1,\n  `
        // afterBrace starts with `\n  "firstField": ...` (indented writer) → trim the leading newline+spaces.
        var trimmed = afterBrace.TrimStart(" \t\r\n"u8);

        var result = new byte[s_prefix.Length + trimmed.Length];
        s_prefix.CopyTo(result, 0);
        trimmed.CopyTo(result.AsSpan(s_prefix.Length));
        return result;
    }

    /// <summary>
    /// Reads the <c>$schemaVersion</c> field from a JSON string.
    /// Returns <c>0</c> when the field is absent (pre-versioned file written before RGAP-9).
    /// </summary>
    internal static int ReadFrom(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("$schemaVersion", out var prop) &&
                prop.TryGetInt32(out var version))
                return version;
        }
        catch (JsonException)
        {
            // Malformed JSON — caller will handle separately.
        }
        return 0;
    }
}
