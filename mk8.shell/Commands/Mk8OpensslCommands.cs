using Mk8.Shell.Safety;

namespace Mk8.Shell;

/// <summary>
/// Read-only OpenSSL x509 certificate inspection templates for
/// <see cref="Mk8CommandWhitelist"/>.
/// <para>
/// Only the <c>x509 -in &lt;file&gt; -noout</c> pattern is registered.
/// The <c>-noout</c> flag prevents binary output. No other OpenSSL
/// subcommand (<c>s_client</c>, <c>enc</c>, <c>genrsa</c>, <c>req</c>,
/// etc.) is whitelisted — those require the dangerous-shell path.
/// </para>
/// <para>
/// These templates parse certificate files already inside the sandbox.
/// No network connection, no key generation, no encryption/decryption.
/// </para>
/// </summary>
public static class Mk8OpensslCommands
{
    internal static KeyValuePair<string, string[]>[] GetWordLists() => [];

    internal static Mk8AllowedCommand[] GetCommands()
    {
        var certPath = new Mk8Slot("cert", Mk8SlotKind.SandboxPath);

        return
        [
            // Full certificate text dump (read-only, no binary output).
            new("openssl x509 text", "openssl",
                ["x509", "-in"], Params: [certPath],
                Flags: [new("-noout"), new("-text")]),

            // Certificate expiry date only.
            new("openssl x509 enddate", "openssl",
                ["x509", "-in"], Params: [certPath],
                Flags: [new("-noout"), new("-enddate")]),

            // Certificate subject only.
            new("openssl x509 subject", "openssl",
                ["x509", "-in"], Params: [certPath],
                Flags: [new("-noout"), new("-subject")]),

            // Certificate issuer only.
            new("openssl x509 issuer", "openssl",
                ["x509", "-in"], Params: [certPath],
                Flags: [new("-noout"), new("-issuer")]),

            // Certificate serial number only.
            new("openssl x509 serial", "openssl",
                ["x509", "-in"], Params: [certPath],
                Flags: [new("-noout"), new("-serial")]),

            // Certificate fingerprint (SHA-256).
            new("openssl x509 fingerprint", "openssl",
                ["x509", "-in"], Params: [certPath],
                Flags: [new("-noout"), new("-fingerprint"), new("-sha256")]),
        ];
    }
}
