using Microsoft.Extensions.Configuration;
using SharpClaw.Configuration;
using SharpClaw.Shared.Instances;
using Supprocom.Secrets;

namespace SharpClaw.Services;

internal static class BundledModuleSetup
{
    public static void RequireOwnedTarget(BackendProcessManager backend)
    {
        if (!backend.IsAvailable || backend.SkipLaunch || backend.IsExternal ||
            (backend.IsRunning && !backend.OwnsCurrentTarget) ||
            !Uri.TryCreate(backend.ApiUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new InvalidOperationException("Module installation here configures only this frontend's bundled Runtime, never an external host.");
    }

    public static Task StopAsync(BackendProcessManager backend, GatewayProcessManager? gateway, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireOwnedTarget(backend);
        gateway?.Stop();
        backend.Stop();
        if (backend.IsRunning) throw new InvalidOperationException("The previous Runtime is still running; no module has been replaced.");
        return Task.CompletedTask;
    }

    public static async Task ConfigureAsync(FrontendInstanceService frontend, string root,
        IReadOnlyList<InstalledModuleIdentity> modules, bool enabled, CancellationToken token)
    {
        var paths = BackendPaths(frontend);
        var store = new SupprocomSecretFileStore(LocalEnvironment.CreateSecretsOptions(
            paths.ConfigDirectory, isDevelopment: false, paths));
        await store.UpdateDocumentAsync(settings => UpdateSettings(settings, root, modules, enabled), token).ConfigureAwait(false);
    }

    internal static IReadOnlyList<SupprocomSecretSetting> UpdateSettings(
        IReadOnlyList<SupprocomSecretSetting> settings, string root, IReadOnlyList<InstalledModuleIdentity> modules, bool enabled)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new InvalidDataException("The installed registration root must exist and be absolute.");
        var values = settings.ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.OrdinalIgnoreCase);
        values["ExternalRegistrations:frontend-modules:Path"] = Path.GetFullPath(root);
        values["ExternalRegistrations:frontend-modules:Enabled"] = "true";
        foreach (var module in modules)
        {
            if (!SharpClawModuleSettings.IsIdentifier(module.Id)) throw new InvalidDataException("Invalid module identity.");
            values[$"Packages:{module.Id}"] = enabled ? "true" : "false";
        }
        return values.Select(pair => new SupprocomSecretSetting(pair.Key, pair.Value)).ToArray();
    }

    public static bool IsEnabled(FrontendInstanceService frontend, string id, bool defaultEnabled = true)
    {
        var paths = BackendPaths(frontend);
        var configuration = new ConfigurationBuilder().AddSupprocomSecrets(
            LocalEnvironment.CreateSecretsOptions(paths.ConfigDirectory, false, paths)).Build();
        try
        {
            return configuration[$"Packages:{id}"] is { } value
                ? bool.TryParse(value, out var enabled) && enabled : defaultEnabled;
        }
        finally { (configuration as IDisposable)?.Dispose(); }
    }

    private static SharpClawInstancePaths BackendPaths(FrontendInstanceService frontend) =>
        new(SharpClawInstanceKind.Backend, frontend.BundledBackendInstanceRoot, frontend.Paths.SharedRoot);
}
