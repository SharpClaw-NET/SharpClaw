using Microsoft.AspNetCore.Http;

namespace SharpClaw.Runtime.Host;

internal static class RemoteProxyHeaders
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Host",
        "Authorization", "Proxy-Authorization", "Proxy-Authenticate", "Cookie", "Set-Cookie",
        "X-Api-Key", "X-Gateway-Key", "X-Gateway-Token", RemoteProxyRoutePolicy.HopHeader,
    };

    internal static HashSet<string> ConnectionTokens(IEnumerable<string?> values)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (value is null)
                continue;
            foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                tokens.Add(token);
        }
        return tokens;
    }

    internal static bool IsAllowed(string name, HashSet<string> connectionTokens) =>
        !Excluded.Contains(name) && !connectionTokens.Contains(name);

    internal static void CopyRequest(HttpRequest incoming, HttpRequestMessage outgoing)
    {
        var tokens = ConnectionTokens(incoming.Headers.Connection);
        foreach (var header in incoming.Headers)
        {
            if (!IsAllowed(header.Key, tokens))
                continue;
            var values = header.Value.ToArray();
            if (!outgoing.Headers.TryAddWithoutValidation(header.Key, values))
                outgoing.Content?.Headers.TryAddWithoutValidation(header.Key, values);
        }
    }

    internal static void CopyResponse(HttpResponseMessage incoming, HttpResponse outgoing)
    {
        var tokens = ConnectionTokens(incoming.Headers.Connection);
        CopyResponseHeaders(incoming.Headers, outgoing.Headers, tokens);
        CopyResponseHeaders(incoming.Content.Headers, outgoing.Headers, tokens);
    }

    internal static void CopyResponseHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> source,
        IHeaderDictionary destination,
        HashSet<string> connectionTokens)
    {
        foreach (var header in source)
        {
            if (IsAllowed(header.Key, connectionTokens))
                destination[header.Key] = header.Value.ToArray();
        }
    }
}
