namespace SharpClaw.Runtime.Host.Routing;


internal sealed class MapDeleteAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "DELETE";
}
