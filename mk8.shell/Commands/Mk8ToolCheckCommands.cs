using Mk8.Shell.Safety;

namespace Mk8.Shell;

/// <summary>
/// Tool existence check templates (<c>which</c> on Linux/macOS,
/// <c>where</c> on Windows) for <see cref="Mk8CommandWhitelist"/>.
/// <para>
/// The binary argument is restricted to a <see cref="Mk8SlotKind.Choice"/>
/// containing ONLY binaries already present in the whitelist. This reveals
/// nothing the agent couldn't already discover by running the binary itself —
/// it just avoids the cryptic "command not found" error on failure.
/// </para>
/// <para>
/// Zero risk: read-only, single typed argument, no path access,
/// no network, no mutation. Returns the binary's PATH location or
/// a "not found" error.
/// </para>
/// </summary>
public static class Mk8ToolCheckCommands
{
    /// <summary>
    /// The set of binaries the agent is allowed to check for existence.
    /// This MUST match the set of binaries that have whitelist templates
    /// elsewhere — checking for a binary that can't be used is pointless.
    /// </summary>
    private static readonly string[] CheckableBinaries =
    [
        "dotnet", "git", "node", "npm", "cargo",
        "tar", "gzip", "gunzip", "zip", "unzip",
        "openssl",
        "cat", "head", "tail", "wc", "sort", "uniq", "diff",
        "sha256sum", "md5sum", "base64",
        "python3", "ruby", "perl", "php",
        "java", "javac", "go", "rustc",
        "swift", "cmake", "gcc", "g++", "clang",
        "docker", "kubectl", "deno", "bun", "terraform",
    ];

    internal static KeyValuePair<string, string[]>[] GetWordLists() => [];

    internal static Mk8AllowedCommand[] GetCommands()
    {
        var binarySlot = new Mk8Slot("binary", Mk8SlotKind.Choice,
            AllowedValues: CheckableBinaries);

        return
        [
            // Linux / macOS
            new("which", "which", [], Params: [binarySlot]),

            // Windows
            new("where", "where", [], Params: [binarySlot]),
        ];
    }
}
