namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Shell types that are considered "safe" — they execute within a
/// sandboxed, verb-restricted environment.  New safe languages can be
/// added here without changing the permission model.
/// <para>
/// <b>ALL mk8.shell execution is safe by definition.</b>  mk8.shell
/// never invokes a real shell interpreter (bash, cmd, powershell).
/// Even its ProcRun and Git* verbs go through binary-allowlist
/// validation, path sandboxing, and argument sanitisation before
/// spawning a process with <c>UseShellExecute = false</c>.  There
/// is no such thing as an "unsafe" mk8.shell execution.
/// </para>
/// <para>
/// Conversely, Bash, PowerShell, CommandPrompt, and Git are
/// <b>always</b> dangerous — see <see cref="DangerousShellType"/>.
/// They are never routed through mk8.shell.
/// </para>
/// </summary>
public enum SafeShellType
{
    Mk8Shell = 0
}
