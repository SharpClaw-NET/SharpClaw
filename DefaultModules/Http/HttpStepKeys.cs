namespace SharpClaw.Modules.Http;

/// <summary>
/// Stable well-known step keys owned by the HTTP module.
/// <para>
/// IMPORTANT: The literal string value intentionally matches the legacy
/// <c>core.http_request</c> value so existing serialized task scripts
/// continue to parse. Only the C# location of the constant changes; the
/// wire format does not.
/// </para>
/// </summary>
public static class HttpStepKeys
{
    /// <summary>Make an HTTP request (shared by HttpGet/HttpPost/HttpPut/HttpDelete).</summary>
    public const string HttpRequest = "core.http_request";
}
