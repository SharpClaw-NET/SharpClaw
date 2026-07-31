namespace SharpClaw.Runtime.Host;

internal static class RemoteRuntimePairingAuthorization
{
    public static void RequireApprovedPair(string? pairingFile)
    {
        _ = pairingFile;
        throw new InvalidOperationException(
            "RemoteProxy mode requires an active approved pairing before binding.");
    }
}
