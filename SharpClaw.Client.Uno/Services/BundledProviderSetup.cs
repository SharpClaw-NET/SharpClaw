using SharpClaw.Configuration;
using SharpClaw.Shared.Instances;
using Supprocom.Secrets;

namespace SharpClaw.Services;

/// <summary>Updates only the local, frontend-owned Runtime configuration.</summary>
internal static class BundledProviderSetup
{
    public static async Task ApplyAsync(
        FrontendInstanceService frontend,
        BackendProcessManager backend,
        GatewayProcessManager? gateway,
        ClientActionDispatcher actions,
        SharpClawProviderSetupOption provider,
        string model,
        string? endpoint,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        if (!backend.OwnsCurrentTarget || backend.SkipLaunch)
            throw new InvalidOperationException("Only the running, frontend-owned bundled Runtime can be configured here.");
        if (string.IsNullOrWhiteSpace(provider.Key))
            throw new ArgumentException("A provider key is required.", nameof(provider));
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (!string.IsNullOrWhiteSpace(endpoint) &&
            (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
             uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new InvalidOperationException("Use an absolute HTTP or HTTPS provider endpoint without embedded credentials.");

        var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend,
            frontend.BundledBackendInstanceRoot, frontend.Paths.SharedRoot);
        var store = new SupprocomSecretFileStore(LocalEnvironment.CreateSecretsOptions(
            paths.ConfigDirectory, isDevelopment: false, paths));
        await actions.RunCommandAsync("client.provider.configure", async token =>
        {
            // Persist a single protected document, preserving unrelated settings.
            // Never silently rebind a previous provider's fallback credentials.
            await store.UpdateDocumentAsync(settings => UpdateSettings(
                settings, provider, model.Trim(), endpoint?.Trim(), credential), token).ConfigureAwait(false);
            gateway?.Stop();
            backend.Stop();
            if (backend.IsRunning)
                throw new InvalidOperationException("The previous Runtime did not stop; it has not been replaced.");
            // The Boot route creates a complete new graph and reads the new
            // protected document. No live singleton or client is mutated.
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<SupprocomSecretSetting> UpdateSettings(
        IReadOnlyList<SupprocomSecretSetting> settings,
        SharpClawProviderSetupOption provider,
        string model,
        string? endpoint,
        string? credential)
    {
        var values = settings.ToDictionary(setting => setting.Key, setting => setting.Value,
            StringComparer.OrdinalIgnoreCase);
        var previousDefault = values.GetValueOrDefault("Provider:Key") ?? values.GetValueOrDefault("Providers:Default");
        var wasDefault = string.Equals(previousDefault, provider.Key, StringComparison.OrdinalIgnoreCase);
        var credentialKey = $"Providers:{provider.Key}:ApiKey";
        var endpointKey = $"Providers:{provider.Key}:Endpoint";
        var effectiveCredential = !string.IsNullOrWhiteSpace(credential) ? credential :
            values.GetValueOrDefault(credentialKey) ?? (wasDefault ? values.GetValueOrDefault("Provider:ApiKey") : null);
        var effectiveEndpoint = !string.IsNullOrWhiteSpace(endpoint) ? endpoint :
            values.GetValueOrDefault(endpointKey) ?? (wasDefault ? values.GetValueOrDefault("Provider:Endpoint") : null);
        if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(effectiveCredential))
            throw new InvalidOperationException("Credentials are required for this provider.");
        if (provider.RequiresEndpoint && string.IsNullOrWhiteSpace(effectiveEndpoint))
            throw new InvalidOperationException("An endpoint is required for this provider.");
        if (!string.IsNullOrWhiteSpace(effectiveEndpoint) &&
            (!Uri.TryCreate(effectiveEndpoint, UriKind.Absolute, out var uri) ||
             uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new InvalidOperationException("Use an absolute HTTP or HTTPS provider endpoint without embedded credentials.");
        // Retain the previous default's values under its own provider identity,
        // rather than either deleting them or sharing them with the new default.
        if (!string.IsNullOrWhiteSpace(previousDefault))
        {
            foreach (var setting in new[] { "ApiKey", "Endpoint" })
            {
                var previousKey = $"Providers:{previousDefault}:{setting}";
                if (!values.ContainsKey(previousKey) && values.TryGetValue($"Provider:{setting}", out var previousValue))
                    values[previousKey] = previousValue;
            }
        }
        values.Remove("Provider:ApiKey");
        values.Remove("Provider:Endpoint");
        values.Remove("Providers:Default");
        values["Provider:Key"] = provider.Key;
        values["Provider:Model"] = model;
        if (effectiveCredential is not null) values[credentialKey] = effectiveCredential;
        if (effectiveEndpoint is not null) values[endpointKey] = effectiveEndpoint;
        return values.Select(pair => new SupprocomSecretSetting(pair.Key, pair.Value)).ToArray();
    }
}
