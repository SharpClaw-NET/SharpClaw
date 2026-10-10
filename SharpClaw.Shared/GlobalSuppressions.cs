using System.Diagnostics.CodeAnalysis;

// These published namespaces predate the analyzer gate. Scope=namespace applies only
// to each namespace symbol; diagnostics for its types and members remain enabled.
[assembly: SuppressMessage("Naming", "CA1716", Scope = "namespace", Target = "~N:SharpClaw.Shared.DurableStorage", Justification = "Renaming the published Shared namespace would break existing source and binary consumers.")]
[assembly: SuppressMessage("Naming", "CA1716", Scope = "namespace", Target = "~N:SharpClaw.Shared.Logging", Justification = "Renaming the published Shared namespace would break existing source and binary consumers.")]
[assembly: SuppressMessage("Naming", "CA1716", Scope = "namespace", Target = "~N:SharpClaw.Shared.Instances", Justification = "Renaming the published Shared namespace would break existing source and binary consumers.")]
[assembly: SuppressMessage("Naming", "CA1716", Scope = "namespace", Target = "~N:SharpClaw.Shared.Security", Justification = "Renaming the published Shared namespace would break existing source and binary consumers.")]
