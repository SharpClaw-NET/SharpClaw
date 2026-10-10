using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SharpClaw.Runtime.Host;

internal static class RemoteProxyRoutePolicy
{
    internal const string HopHeader = "X-SharpClaw-Proxy-Hop";

    internal static bool IsAllowed(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey(HopHeader))
            return false;
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(rawTarget))
        {
            var queryIndex = rawTarget.IndexOf('?', StringComparison.Ordinal);
            if (!IsSafePath(queryIndex < 0 ? rawTarget : rawTarget[..queryIndex]))
                return false;
        }

        var path = context.Request.Path.ToUriComponent();
        if (!IsSafePath(path))
            return false;
        var relativePath = GetRelativePath(path);
        var segmentEnd = relativePath.IndexOf('/', StringComparison.Ordinal);
        var firstSegment = Uri.UnescapeDataString(segmentEnd < 0 ? relativePath : relativePath[..segmentEnd]);
        return !IsControlSegment(firstSegment)
            || string.Equals(path, "/remote/status", StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetRelativePath(string path) => path.TrimStart('/');

    private static bool IsControlSegment(string segment) =>
        segment.Equals("env", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("remote", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("internal", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("control", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("configuration", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("diagnostics", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("bootstrap", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafePath(string path)
    {
        if (!path.StartsWith('/') || path.Contains("//", StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal))
            return false;
        foreach (var segment in path.Split('/'))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded.Equals(".", StringComparison.Ordinal) || decoded.Equals("..", StringComparison.Ordinal)
                || decoded.Contains('/', StringComparison.Ordinal)
                || decoded.Contains('\\', StringComparison.Ordinal)
                || decoded.Contains('%', StringComparison.Ordinal)
                || decoded.Any(char.IsControl))
                return false;
        }
        return true;
    }
}
