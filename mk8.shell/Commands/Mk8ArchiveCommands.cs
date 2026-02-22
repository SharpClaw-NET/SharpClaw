using Mk8.Shell.Safety;

namespace Mk8.Shell;

/// <summary>
/// Archive tool (<c>tar</c>, <c>gzip</c>, <c>gunzip</c>, <c>zip</c>,
/// <c>unzip</c>) command templates for <see cref="Mk8CommandWhitelist"/>.
/// <para>
/// <b>Create and list ONLY — extraction is not whitelisted</b> because
/// archives can contain symlinks pointing outside the sandbox or path
/// traversal entries (<c>../../etc/cron.d/...</c>).
/// <see cref="Mk8PathSanitizer"/> validates mk8.shell verb arguments
/// but cannot inspect archive contents.
/// </para>
/// <para>
/// All data is compile-time constant.  To add or modify allowed commands,
/// edit this file and recompile — there is no runtime registration.
/// </para>
/// </summary>
public static class Mk8ArchiveCommands
{
    internal static KeyValuePair<string, string[]>[] GetWordLists() => [];

    internal static Mk8AllowedCommand[] GetCommands()
    {
        var pathSlot = new Mk8Slot("path", Mk8SlotKind.SandboxPath);
        var variadicPaths = new Mk8Slot("inputs", Mk8SlotKind.SandboxPath, Variadic: true);

        return
        [
            // tar — list and create only
            new("tar list", "tar", ["-tf"], Params: [pathSlot]),
            new("tar list verbose", "tar", ["-tvf"], Params: [pathSlot]),
            new("tar create", "tar", ["-cf"], Params: [pathSlot, variadicPaths]),
            new("tar create gzip", "tar", ["-czf"], Params: [pathSlot, variadicPaths]),

            // gzip / gunzip — in-place compression
            new("gzip", "gzip", [], Params: [pathSlot]),
            new("gunzip", "gunzip", [], Params: [pathSlot]),

            // zip — create only
            new("zip create", "zip", [], Params: [pathSlot, variadicPaths]),

            // unzip — list only
            new("unzip list", "unzip", ["-l"], Params: [pathSlot]),
        ];
    }
}
