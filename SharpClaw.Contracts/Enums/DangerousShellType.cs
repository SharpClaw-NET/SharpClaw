namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Shell types that are considered "dangerous" — they spawn a real
/// shell interpreter or process with arbitrary, unvalidated command
/// execution.  Require system-user credentials plus elevated clearance.
/// <para>
/// <b>ALL Bash, PowerShell, CommandPrompt, and Git usage is dangerous
/// by definition.</b>  These shells are never sandboxed through
/// mk8.shell and are never considered "safe".  The raw command text
/// is handed directly to the interpreter — the only protection is the
/// permission system's clearance requirement.
/// </para>
/// <para>
/// The safe counterpart is <see cref="SafeShellType.Mk8Shell"/>,
/// which is a closed-verb, sandboxed DSL that never invokes a real
/// shell interpreter.
/// </para>
/// </summary>
public enum DangerousShellType
{
    Bash = 0,
    PowerShell = 1,
    CommandPrompt = 2,
    Git = 3
}
