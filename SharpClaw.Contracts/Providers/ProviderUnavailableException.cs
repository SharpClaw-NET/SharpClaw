namespace SharpClaw.Contracts.Providers;

/// <summary>
/// Thrown when the provider key on a <c>ProviderDB</c> row references a
/// plugin that is not currently registered in DI — typically because the
/// owning module has been disabled or uninstalled. Carries the missing
/// provider key so callers can surface an actionable
/// <em>"enable module X to use this provider"</em> message instead of a
/// generic <see cref="NotSupportedException"/>.
/// </summary>
public sealed class ProviderUnavailableException : Exception
{
    public ProviderUnavailableException(string providerKey)
        : base($"Provider '{providerKey}' is not available. The owning provider module is disabled or not installed.")
    {
        ProviderKey = providerKey;
    }

    public string ProviderKey { get; }
}
