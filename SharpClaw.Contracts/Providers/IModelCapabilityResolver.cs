namespace SharpClaw.Contracts.Providers;

/// <summary>
/// Resolves the capability tag set for a model name belonging to a
/// specific provider plugin. Replaces the centralised
/// <c>IsChatCapable</c> / <c>IsVisionCapable</c> ladders previously
/// hard-coded on <c>ProviderService</c>.
/// </summary>
public interface IModelCapabilityResolver
{
    /// <summary>
    /// Returns the inferred capability tag keys (see
    /// <c>WellKnownCapabilityKeys</c>) for the given model identifier.
    /// Implementations should be conservative — when a model name does
    /// not match any known family, return an empty set.
    /// </summary>
    HashSet<string> Resolve(string modelName);
}
