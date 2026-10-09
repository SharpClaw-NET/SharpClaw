namespace SharpClaw.Runtime.Host.Routing;


[AttributeUsage(AttributeTargets.Method)]
internal abstract class MapMethodAttribute(string pattern) : Attribute
{
    public string Pattern { get; } = pattern;
    public abstract string HttpMethod { get; }
}
