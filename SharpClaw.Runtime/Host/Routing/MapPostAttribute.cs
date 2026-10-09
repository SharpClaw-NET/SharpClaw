namespace SharpClaw.Runtime.Host.Routing;


internal sealed class MapPostAttribute(string pattern = "") : MapMethodAttribute(pattern)
{
    public override string HttpMethod => "POST";
}
