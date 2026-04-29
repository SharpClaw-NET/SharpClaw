namespace SharpClaw.Modules.Providers.LlamaSharp.LocalInference;

/// <summary>
/// GBNF fragments for the subset of JSON Schema <c>format</c> keyword
/// values that <see cref="LlamaSharpJsonSchemaConverter"/> can
/// translate into a constrained grammar rule.
/// <para>
/// Each entry describes the top-level body for a quoted JSON string of
/// the given format (including the surrounding <c>"</c> characters)
/// plus any helper sub-rules needed. The converter emits helpers via
/// <c>EmitNamedRule</c> so they share the global rule table and can be
/// deduplicated when the same format appears multiple times.
/// </para>
/// <para>
/// Coverage is intentionally narrow: only formats with a compact
/// regular grammar. Everything else falls back to the generic
/// <c>string</c> primitive.
/// </para>
/// </summary>
internal static class LlamaSharpStringFormatGrammars
{
    /// <summary>A fragment of a format grammar.</summary>
    /// <param name="TopBody">Body of the top-level rule (no name).</param>
    /// <param name="Helpers">Helper rules referenced by <see cref="TopBody"/>.</param>
    internal sealed record Fragment(
        string TopBody,
        IReadOnlyList<(string Hint, string Body)> Helpers);

    private const string Hex = "[0-9a-fA-F]";
    private const string Alnum = "[A-Za-z0-9]";

    /// <summary>
    /// Returns the grammar fragment for a supported <paramref name="format"/>
    /// or <c>null</c> when the format is not covered.
    /// </summary>
    public static Fragment? TryGet(string format) => format switch
    {
        "uuid" => Uuid,
        "email" => Email,
        "date" => Date,
        "date-time" => DateTime,
        "time" => Time,
        "ipv4" => Ipv4,
        "uri" or "uri-reference" => Uri,
        "hostname" => Hostname,
        _ => null,
    };

    // 8-4-4-4-12 hex groups, RFC 4122 shape (no version/variant check).
    private static readonly Fragment Uuid = new(
        $"\"\\\"\" {Hex}{{8}} \"-\" {Hex}{{4}} \"-\" {Hex}{{4}} \"-\" {Hex}{{4}} \"-\" {Hex}{{12}} \"\\\"\"",
        Array.Empty<(string, string)>());

    // Pragmatic email: local@label(.label)+. Labels alphanumeric/hyphen.
    private static readonly Fragment Email = new(
        "\"\\\"\" fmt-email-local \"@\" fmt-email-domain \"\\\"\"",
        new (string Hint, string Body)[]
        {
            ("fmt-email-local",  "[A-Za-z0-9._%+-]+"),
            ("fmt-email-domain", "fmt-email-label ( \".\" fmt-email-label )+"),
            ("fmt-email-label",  $"{Alnum} ( {Alnum} | \"-\" )*"),
        });

    // YYYY-MM-DD — grammar enforces shape only, not calendar validity.
    private static readonly Fragment Date = new(
        "\"\\\"\" [0-9] [0-9] [0-9] [0-9] \"-\" [0-9] [0-9] \"-\" [0-9] [0-9] \"\\\"\"",
        Array.Empty<(string, string)>());

    // HH:MM:SS with optional fractional seconds and timezone.
    private static readonly Fragment Time = new(
        "\"\\\"\" fmt-time-body \"\\\"\"",
        new (string Hint, string Body)[]
        {
            ("fmt-time-body", "[0-9] [0-9] \":\" [0-9] [0-9] \":\" [0-9] [0-9] ( \".\" [0-9]+ )? fmt-time-zone?"),
            ("fmt-time-zone", "\"Z\" | ( \"+\" | \"-\" ) [0-9] [0-9] \":\" [0-9] [0-9]"),
        });

    // RFC 3339 date-time: date 'T' time with required timezone.
    private static readonly Fragment DateTime = new(
        "\"\\\"\" [0-9] [0-9] [0-9] [0-9] \"-\" [0-9] [0-9] \"-\" [0-9] [0-9] " +
        "\"T\" [0-9] [0-9] \":\" [0-9] [0-9] \":\" [0-9] [0-9] " +
        "( \".\" [0-9]+ )? ( \"Z\" | ( \"+\" | \"-\" ) [0-9] [0-9] \":\" [0-9] [0-9] ) \"\\\"\"",
        Array.Empty<(string, string)>());

    // Dotted-quad. Each octet 1-3 digits; value range not enforced.
    private static readonly Fragment Ipv4 = new(
        "\"\\\"\" fmt-ip4-octet \".\" fmt-ip4-octet \".\" fmt-ip4-octet \".\" fmt-ip4-octet \"\\\"\"",
        new (string Hint, string Body)[]
        {
            ("fmt-ip4-octet", "[0-9] | [0-9] [0-9] | [0-9] [0-9] [0-9]"),
        });

    // Relaxed URI: scheme ":" then any non-quote non-backslash chars.
    private static readonly Fragment Uri = new(
        "\"\\\"\" fmt-uri-scheme \":\" fmt-uri-body \"\\\"\"",
        new (string Hint, string Body)[]
        {
            ("fmt-uri-scheme", "[A-Za-z] [A-Za-z0-9+.-]*"),
            ("fmt-uri-body",   "[^\"\\\\]+"),
        });

    // RFC 1123 hostname: labels joined by '.', each alphanumeric/hyphen,
    // not starting or ending with a hyphen. Grammar enforces shape only.
    private static readonly Fragment Hostname = new(
        "\"\\\"\" fmt-host-label ( \".\" fmt-host-label )* \"\\\"\"",
        new (string Hint, string Body)[]
        {
            ("fmt-host-label", $"{Alnum} ( ( {Alnum} | \"-\" )* {Alnum} )?"),
        });
}
