using Mk8.Shell.Safety;

namespace Mk8.Shell;

/// <summary>
/// Runtime configuration provided by the administrator at startup.
/// <para>
/// These are the ONLY values that can be set outside of compile-time
/// source files.  They are passed to
/// <see cref="Mk8CommandWhitelist.CreateDefault(Mk8RuntimeConfig?)"/>
/// and baked into the immutable whitelist at construction time —
/// they cannot be changed after creation.
/// </para>
/// <para>
/// <b>Why this exception exists:</b> project names and git remote URLs
/// are environment-specific (e.g., "SharpClaw", "BananaApp",
/// "https://github.com/org/repo.git").  Pre-enumerating all possible
/// values at compile time is impractical.  These are one-off setup
/// operations with limited abuse surface — unlike commit messages or
/// branch names which are used repeatedly.
/// </para>
/// </summary>
public sealed class Mk8RuntimeConfig
{
    /// <summary>
    /// Base project names the agent may use with <c>dotnet new -n</c>.
    /// Combined with compile-time suffixes from
    /// <see cref="Mk8DotnetCommands.ProjectSuffixes"/> to form compound
    /// names like <c>"BananaApi"</c> or <c>"Banana.Api"</c>.
    /// <para>
    /// The base alone is also valid: <c>"Banana"</c>.
    /// </para>
    /// </summary>
    public string[] ProjectBases { get; init; } = [];

    /// <summary>
    /// Allowed git remote URLs the agent may use with
    /// <c>git remote add &lt;name&gt; &lt;url&gt;</c>.
    /// <para>
    /// Only HTTPS URLs should be used — SSH URLs bypass TLS certificate
    /// validation and could connect to unexpected hosts.
    /// </para>
    /// </summary>
    public string[] GitRemoteUrls { get; init; } = [];

    /// <summary>
    /// Allowed git clone URLs the agent may use with
    /// <c>git clone &lt;url&gt;</c>.
    /// <para>
    /// Only HTTPS URLs should be used — SSH URLs bypass TLS certificate
    /// validation and could connect to unexpected hosts.  Each URL must
    /// point to a repository the agent is explicitly permitted to clone.
    /// </para>
    /// </summary>
    public string[] GitCloneUrls { get; init; } = [];
}
