using Mk8.Shell.Engine;
using Mk8.Shell.Models;

namespace Mk8.Shell.Safety;

/// <summary>
/// Resolves and validates filesystem paths against a sandbox root.
/// <para>
/// Core rule: <b>resolve first, check second.</b>
/// </para>
/// </summary>
public static class Mk8PathSanitizer
{
    /// <summary>
    /// GIGABLACKLISTED filenames — mk8.shell commands must NEVER read,
    /// write, modify, or delete these files under any circumstances.
    /// They are only managed by the user directly or by mk8.shell.startup.
    /// Checked on ALL path resolutions (read and write).
    /// </summary>
    private static readonly HashSet<string> GigaBlacklistedFilenames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        Mk8SandboxRegistry.SandboxEnvFileName,
        Mk8SandboxRegistry.SandboxSignedEnvFileName,
    };

    /// <summary>
    /// File extensions that remain blocked on write targets because
    /// an allowed binary on the ProcRun allowlist could directly execute
    /// them, or because they are native executables the OS runs directly.
    /// <para>
    /// <b>NOT blocked here</b>: script extensions whose interpreters are
    /// ALL permanently blocked (e.g. .sh, .bat, .ps1, .py). Agents can
    /// author these for humans or unrestricted external tooling — the
    /// agent itself has no way to run them through mk8.shell.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> BlockedWriteExtensions =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Native executables (OS runs directly) ─────────────────
        ".exe", ".com", ".scr", ".msi", ".msp", ".dll",
        ".bin", ".run", ".appimage", ".elf", ".so", ".dylib",

        // ── Extensions executable by ALLOWED ProcRun binaries ─────
        // node (on allowlist) can run these directly
        ".js", ".mjs", ".cjs",
        // Windows script host (could be invoked by OS association)
        ".jse", ".wsf", ".wsh", ".msh", ".vbs", ".vbe",

        // ── MSBuild project files (dotnet build executes <Exec> targets) ──
        // dotnet is on the allowlist; these files trigger arbitrary
        // command execution via MSBuild <Exec>, <Target>, source
        // generators, and pre/post build events.
        ".csproj", ".fsproj", ".vbproj", ".proj",
        ".targets", ".props", ".sln",

        // ── Cargo build scripts (cargo build executes build.rs) ───
        ".rs",
    };

    public static string Resolve(string userPath, string sandboxRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxRoot);

        ValidateCharacters(userPath);

        var canonicalRoot = NormalizeSeparators(Path.GetFullPath(sandboxRoot));
        if (!canonicalRoot.EndsWith(Path.DirectorySeparatorChar))
            canonicalRoot += Path.DirectorySeparatorChar;

        var combined = Path.IsPathRooted(userPath)
            ? Path.GetFullPath(userPath)
            : Path.GetFullPath(Path.Combine(canonicalRoot, userPath));
        var canonical = NormalizeSeparators(combined);

        if (!canonical.StartsWith(canonicalRoot, PathComparison)
            && !string.Equals(canonical + Path.DirectorySeparatorChar, canonicalRoot, PathComparison))
        {
            throw new Mk8PathViolationException(userPath, sandboxRoot);
        }

        if (OperatingSystem.IsWindows())
            ValidateNoDeviceName(canonical);

        ValidateNotGigaBlacklisted(canonical, userPath);

        return canonical;
    }

    /// <summary>
    /// Validates a path that will be the TARGET of a write, append,
    /// move, or copy operation. Blocks:
    /// <list type="bullet">
    ///   <item>Native executables and code files runnable by allowed binaries</item>
    ///   <item>Config filenames that trigger implicit code execution (e.g. Makefile)</item>
    /// </list>
    /// <para>
    /// Script extensions whose interpreters are ALL permanently blocked
    /// (.sh, .bat, .ps1, .py, etc.) are intentionally ALLOWED — agents
    /// can author these for humans or external tooling.
    /// </para>
    /// </summary>
    public static string ResolveForWrite(string userPath, string sandboxRoot)
    {
        var resolved = Resolve(userPath, sandboxRoot);
        ValidateNotGitInternals(resolved, userPath);
        ValidateNotExecutableExtension(resolved, userPath);
        ValidateNotDangerousFilename(resolved, userPath);
        return resolved;
    }

    /// <summary>
    /// Returns <c>true</c> if the path resolves inside the sandbox.
    /// </summary>
    public static bool IsInsideSandbox(string userPath, string sandboxRoot)
    {
        try
        {
            Resolve(userPath, sandboxRoot);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════

    private static void ValidateCharacters(string path)
    {
        foreach (var c in path)
        {
            if (c == '\0')
                throw new ArgumentException(
                    "Path contains a null byte (0x00). Paths must contain only " +
                    "printable characters.\n" +
                    "  ✓ Correct: \"$WORKSPACE/src/app.cs\"\n" +
                    "  ✗ Wrong:   a path with embedded null bytes", nameof(path));
            if (char.IsControl(c) && c != '\t')
                throw new ArgumentException(
                    $"Path contains control character (U+{(int)c:X4}). Only printable " +
                    "characters and tabs are allowed in paths.\n" +
                    "  ✓ Correct: \"$WORKSPACE/src/app.cs\"\n" +
                    "  ✗ Wrong:   paths with newlines, carriage returns, or escape sequences",
                    nameof(path));
        }
    }

    /// <summary>
    /// Blocks writes to <c>.git/</c> internal paths.  Prevents:
    /// <list type="bullet">
    ///   <item>Hook injection (<c>.git/hooks/pre-commit</c>)</item>
    ///   <item>Config tampering (<c>.git/config</c>)</item>
    ///   <item>Object injection (<c>.git/objects/...</c>)</item>
    ///   <item>HEAD manipulation (<c>.git/HEAD</c>)</item>
    /// </list>
    /// </summary>
    private static void ValidateNotGitInternals(
        string resolvedPath, string originalPath)
    {
        var sep = Path.DirectorySeparatorChar;
        var gitSegment = $"{sep}.git{sep}";

        if (resolvedPath.Contains(gitSegment, PathComparison)
            || resolvedPath.EndsWith($"{sep}.git", PathComparison))
        {
            throw new Mk8CompileException(Mk8ShellVerb.FileWrite,
                $"Cannot write to git internals (.git/): '{originalPath}'.\n" +
                "The .git/ directory contains hooks, config, objects, and refs " +
                "that must only be modified through git commands — never through " +
                "direct file writes (which could inject malicious hooks or tamper " +
                "with repository state).\n" +
                "  ✓ Correct: { \"verb\": \"FileWrite\", \"args\": [\"$WORKSPACE/src/readme.md\", \"...\"] }\n" +
                "  ✗ Wrong:   { \"verb\": \"FileWrite\", \"args\": [\"$WORKSPACE/.git/hooks/pre-commit\", \"...\"] }\n" +
                "Use git commands (via ProcRun) for git operations. " +
                "Run { \"verb\": \"Mk8Templates\", \"args\": [] } to see available git templates.");
        }
    }

    private static void ValidateNotExecutableExtension(
        string resolvedPath, string originalPath)
    {
        var ext = Path.GetExtension(resolvedPath);
        if (!string.IsNullOrEmpty(ext) && BlockedWriteExtensions.Contains(ext))
            throw new Mk8CompileException(Mk8ShellVerb.FileWrite,
                $"Cannot write to executable file type '{ext}': '{originalPath}'.\n" +
                "This extension is blocked because the file could be executed by the OS " +
                "or by an allowed binary (dotnet, node, cargo) without the agent invoking " +
                "it — other processes sharing the sandbox could load it implicitly.\n" +
                "  ✓ Correct: .txt, .json, .yaml, .xml, .csv, .md, .sh, .py, .ps1 (scripts OK — interpreters blocked)\n" +
                $"  ✗ Wrong:   {ext} (blocked: native executable, code runnable by allowed binary, or MSBuild project)\n" +
                "If you need to create a configuration or data file, use a safe extension. " +
                "Run { \"verb\": \"Mk8Docs\", \"args\": [] } to see the full write-protection model.");
    }

    /// <summary>
    /// Filenames that trigger code execution by proximity — just
    /// existing in a directory causes allowed build tools to run
    /// whatever is inside them.
    /// </summary>
    private static readonly HashSet<string> BlockedWriteFilenames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // make / cmake
        "Makefile", "makefile", "GNUmakefile",
        "CMakeLists.txt",
        "Dockerfile",
        ".npmrc",

        // dotnet — MSBuild targets can contain <Exec Command="..."/>
        "Directory.Build.props", "Directory.Build.targets",
        "Directory.Packages.props",
        "nuget.config", "NuGet.Config", "NuGet.config",

        // npm / node — lifecycle scripts run during install
        "package.json",

        // cargo — build.rs runs at build time
        "build.rs", "Cargo.toml",

        // python — pip3 executes during install
        "setup.py", "setup.cfg", "pyproject.toml",

        // git — .gitattributes can redirect filter drivers (clean/smudge)
        // to different file patterns if .git/config has driver definitions.
        // .gitmodules can redirect submodule URLs.
        ".gitattributes", ".gitmodules",

        // mk8.shell sandbox env files — also in GigaBlacklistedFilenames
        // but listed here too for defence-in-depth on the write path.
        Mk8SandboxRegistry.SandboxEnvFileName,
        Mk8SandboxRegistry.SandboxSignedEnvFileName,
    };

    private static void ValidateNotDangerousFilename(
        string resolvedPath, string originalPath)
    {
        var fileName = Path.GetFileName(resolvedPath);
        if (BlockedWriteFilenames.Contains(fileName))
            throw new Mk8CompileException(Mk8ShellVerb.FileWrite,
                $"Cannot write to config file '{fileName}': '{originalPath}'.\n" +
                "This filename triggers implicit code execution by build tools " +
                "(e.g., Makefile by make, package.json by npm, Cargo.toml by cargo, " +
                ".csproj by dotnet build). Writing to it could cause arbitrary code " +
                "execution the next time the build tool runs in this directory.\n" +
                "  ✓ Correct: { \"verb\": \"FileWrite\", \"args\": [\"$WORKSPACE/config.yaml\", \"...\"] }\n" +
                $"  ✗ Wrong:   {{ \"verb\": \"FileWrite\", \"args\": [\"$WORKSPACE/{fileName}\", \"...\"] }}\n" +
                "Use a non-dangerous filename, or use FileTemplate/FilePatch to modify " +
                "existing safe files instead.");
    }

    /// <summary>
    /// GIGABLACKLIST enforcement. These files are completely off-limits
    /// to mk8.shell commands — no read, no write, no copy, no move,
    /// no delete, no hash, no list, nothing. Only the user or
    /// mk8.shell.startup may touch them.
    /// </summary>
    private static void ValidateNotGigaBlacklisted(
        string resolvedPath, string originalPath)
    {
        var fileName = Path.GetFileName(resolvedPath);
        if (GigaBlacklistedFilenames.Contains(fileName))
            throw new Mk8CompileException(Mk8ShellVerb.FileRead,
                $"Access to '{fileName}' is permanently forbidden.\n" +
                "mk8.shell sandbox environment files (mk8.shell.env, " +
                "mk8.shell.signed.env) can only be managed by the user " +
                "directly or by mk8.shell.startup. No mk8.shell command " +
                "may read, write, copy, move, delete, hash, or list them.\n" +
                $"  ✗ Wrong: any operation targeting '{originalPath}'\n" +
                "These files contain sandbox configuration and cryptographic " +
                "signatures. Use { \"verb\": \"Mk8Env\", \"args\": [] } to " +
                "see your merged environment variables instead.");
    }

    private static readonly HashSet<string> WindowsDeviceNames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4",
        "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4",
        "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static void ValidateNoDeviceName(string canonical)
    {
        var fileName = Path.GetFileNameWithoutExtension(canonical);
        if (fileName is not null && WindowsDeviceNames.Contains(fileName))
            throw new ArgumentException(
                $"Path contains reserved Windows device name '{fileName}'.");
    }

    private static string NormalizeSeparators(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
