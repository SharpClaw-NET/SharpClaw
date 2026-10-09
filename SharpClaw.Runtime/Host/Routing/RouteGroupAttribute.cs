namespace SharpClaw.Runtime.Host.Routing;


[AttributeUsage(AttributeTargets.Class)]
internal sealed class RouteGroupAttribute(string prefix) : Attribute
{
    public string Prefix { get; } = prefix;
}
