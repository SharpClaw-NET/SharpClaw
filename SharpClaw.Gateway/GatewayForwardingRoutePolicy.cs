using Microsoft.AspNetCore.Http.Features;

namespace SharpClaw.Gateway;

/// <summary>Public Gateway transport cannot grant access to Runtime control paths.</summary>
internal static class GatewayForwardingRoutePolicy
{
    internal static bool IsAllowed(HttpContext context)
    {
        var path = context.Request.Path.ToUriComponent();
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return true;
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(raw))
        {
            var query = raw.IndexOf('?', StringComparison.Ordinal);
            if (!IsSafePath(query < 0 ? raw : raw[..query]))
                return false;
        }
        return IsSafePath(path) && IsPublicRuntimePath(path[4..]);
    }

    private static bool IsPublicRuntimePath(string path)
    {
        if (path.Equals("/remote/status", StringComparison.OrdinalIgnoreCase))
            return true;
        var segment = Uri.UnescapeDataString(path.TrimStart('/').Split('/')[0]);
        return !segment.Equals("env", StringComparison.OrdinalIgnoreCase)
            && !segment.Equals("remote", StringComparison.OrdinalIgnoreCase)
            && !segment.Equals("internal", StringComparison.OrdinalIgnoreCase)
            && !segment.Equals("control", StringComparison.OrdinalIgnoreCase)
            && !segment.Equals("configuration", StringComparison.OrdinalIgnoreCase)
            && !segment.Equals("diagnostics", StringComparison.OrdinalIgnoreCase)
            && !segment.Equals("bootstrap", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafePath(string path)
    {
        if (!path.StartsWith('/') || path.Contains("//", StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal))
            return false;
        foreach (var segment in path.Split('/'))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded.Equals(".", StringComparison.Ordinal) || decoded.Equals("..", StringComparison.Ordinal)
                || decoded.Contains('/', StringComparison.Ordinal) || decoded.Contains('\\', StringComparison.Ordinal)
                || decoded.Contains('%', StringComparison.Ordinal) || decoded.Any(char.IsControl))
                return false;
        }
        return true;
    }
}
