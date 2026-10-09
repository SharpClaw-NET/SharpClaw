namespace SharpClaw.Runtime.Host.Routing;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class RouteGroupAttribute(string prefix) : Attribute
{
    public string Prefix { get; } = prefix;
}

[AttributeUsage(AttributeTargets.Method)]
internal abstract class MapMethodAttribute(string pattern) : Attribute
{
    public string Pattern { get; } = pattern;
    public abstract string HttpMethod { get; }
}

internal sealed class MapGetAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "GET";
}

internal sealed class MapPostAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "POST";
}

internal sealed class MapPutAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "PUT";
}

internal sealed class MapDeleteAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "DELETE";
}
