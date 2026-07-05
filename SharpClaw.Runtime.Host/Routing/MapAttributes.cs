namespace SharpClaw.Runtime.Host.Routing;

[AttributeUsage(AttributeTargets.Class)]
public sealed class RouteGroupAttribute(string prefix) : Attribute
{
    public string Prefix { get; } = prefix;
}

[AttributeUsage(AttributeTargets.Method)]
public abstract class MapMethodAttribute(string pattern) : Attribute
{
    public string Pattern { get; } = pattern;
    public abstract string HttpMethod { get; }
}

public sealed class MapGetAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "GET";
}

public sealed class MapPostAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "POST";
}

public sealed class MapPutAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "PUT";
}

public sealed class MapDeleteAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "DELETE";
}
