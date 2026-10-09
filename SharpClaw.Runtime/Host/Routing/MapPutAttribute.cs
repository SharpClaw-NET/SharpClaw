namespace SharpClaw.Runtime.Host.Routing;


internal sealed class MapPutAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "PUT";
}
