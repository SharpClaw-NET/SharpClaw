using Mk8.Shell.Engine;

namespace Mk8.Shell;

/// <summary>
/// Thrown when the compiler encounters an invalid verb or argument set.
/// </summary>
public sealed class Mk8CompileException(
    Mk8ShellVerb verb, string reason)
    : InvalidOperationException($"mk8.shell compile error [{verb}]: {reason}")
{
    public Mk8ShellVerb Verb { get; } = verb;
    public string Reason { get; } = reason;
}

/// <summary>
/// Thrown when a path resolves outside the allowed sandbox directory.
/// </summary>
public sealed class Mk8PathViolationException(
    string attemptedPath, string sandboxRoot)
    : InvalidOperationException(
        $"mk8.shell path violation: Path '{attemptedPath}' resolves outside the sandbox " +
        $"root '{sandboxRoot}'. All paths must stay within the sandbox directory.\n" +
        $"  ✓ Correct: \"$WORKSPACE/src/app.cs\" (stays inside sandbox)\n" +
        $"  ✗ Wrong:   \"/etc/passwd\" or \"../../outside\" (escapes sandbox)\n" +
        $"Use $WORKSPACE as the base for all paths. " +
        $"Run {{ \"verb\": \"Mk8Info\", \"args\": [] }} to see the current sandbox root.")
{
    public string AttemptedPath { get; } = attemptedPath;
    public string SandboxRoot { get; } = sandboxRoot;
}

/// <summary>
/// Thrown when a URL fails mk8.shell security validation.
/// </summary>
public sealed class Mk8UrlViolationException(
    string attemptedUrl, string reason)
    : InvalidOperationException(
        $"mk8.shell URL violation: '{attemptedUrl}' — {reason}\n" +
        $"  ✓ Correct: \"https://api.example.com/data\" (public HTTPS, port 443)\n" +
        $"  ✗ Wrong:   \"http://169.254.169.254/\" (metadata endpoint)\n" +
        $"  ✗ Wrong:   \"http://10.0.0.1/\" (private IP)\n" +
        $"  ✗ Wrong:   \"ftp://files.example.com\" (unsupported scheme)\n" +
        "URLs must use https (or http if opted in), target public hosts, " +
        "and use port 80 or 443. No private IPs, metadata endpoints, or credentials in URL.")
{
    public string AttemptedUrl { get; } = attemptedUrl;
    public string Reason { get; } = reason;
}

/// <summary>
/// Thrown when a gigablacklisted pattern is detected anywhere in a
/// command invocation. This is an unconditional, non-bypassable safety
/// check that runs before compilation.
/// </summary>
public sealed class Mk8GigaBlacklistException(
    string foundTerm, string context)
    : InvalidOperationException(
        $"Gigablacklisted term not allowed: [{foundTerm}] — {context}\n" +
        "The gigablacklist blocks dangerous patterns in ALL arguments of ALL commands. " +
        "This includes shell injection markers, destructive commands, " +
        "sandbox env filenames, and more. This check cannot be bypassed.\n" +
        $"  ✗ The term \"{foundTerm}\" was found in your argument.\n" +
        "Run { \"verb\": \"Mk8Blacklist\", \"args\": [] } to see all blocked patterns. " +
        "Rephrase your argument to avoid the blocked term.")
{
    public string FoundTerm { get; } = foundTerm;
    public string Context { get; } = context;
}
