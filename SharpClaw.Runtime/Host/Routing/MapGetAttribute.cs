namespace SharpClaw.Runtime.Host.Routing;


internal sealed class MapGetAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "GET";
}
