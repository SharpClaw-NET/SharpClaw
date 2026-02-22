using Mk8.Shell.Safety;

namespace Mk8.Shell;

/// <summary>
/// Version-check command templates for runtimes whose binaries are
/// otherwise permanently blocked. These use the version-check
/// exception in <see cref="Mk8BinaryAllowlist.IsVersionCheckException"/>
/// to bypass the block — ONLY the exact <c>--version</c> pattern is
/// allowed; all other arguments remain blocked.
/// <para>
/// Also includes version checks for non-blocked runtimes (java, go,
/// rustc) that simply need a whitelist template.
/// </para>
/// </summary>
public static class Mk8VersionCheckCommands
{
    internal static KeyValuePair<string, string[]>[] GetWordLists() => [];

    internal static Mk8AllowedCommand[] GetCommands() =>
    [
        // Blocked binaries with version-check exception:
        new("python3 version", "python3", ["--version"]),
        new("ruby version",    "ruby",    ["--version"]),
        new("perl version",    "perl",    ["--version"]),
        new("php version",     "php",     ["--version"]),

        // Non-blocked binaries — just need a whitelist template:
        new("java version",    "java",    ["--version"]),
        new("javac version",   "javac",   ["--version"]),
        new("go version",      "go",      ["version"]),
        new("rustc version",   "rustc",   ["--version"]),
        new("swift version",   "swift",   ["--version"]),
        new("cmake version",   "cmake",   ["--version"]),
        new("gcc version",     "gcc",     ["--version"]),
        new("g++ version",     "g++",     ["--version"]),
        new("clang version",   "clang",   ["--version"]),
        new("docker version",  "docker",  ["--version"]),
        new("kubectl client version", "kubectl", ["version", "--client"]),
        new("deno version",    "deno",    ["--version"]),
        new("bun version",     "bun",     ["--version"]),
        new("terraform version", "terraform", ["--version"]),
    ];
}
