using Microsoft.Extensions.Configuration;
using SharpClaw.Configuration;
using SharpClaw.Shared.Instances;
using Supprocom.Secrets;

namespace SharpClaw.Services;

internal static class BundledModuleSetup
{
    internal static void RequireLocalMode(FrontendInstanceService frontend)
    {
        if (RemoteBackendConnectionService.IsProxyConfigured(frontend))
            throw new InvalidOperationException("Disconnect the remote backend before configuring or activating local modules and providers.");
    }

    public static void RequireOwnedTarget(BackendProcessManager backend)
    {
        if (!backend.IsAvailable || backend.SkipLaunch || backend.IsExternal ||
            (backend.IsRunning && !backend.OwnsCurrentTarget) ||
            !Uri.TryCreate(backend.ApiUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new InvalidOperationException("Module installation here configures only this frontend's bundled Runtime, never an external host.");
    }

    public static Task ConfigureAsync(FrontendInstanceService frontend, string root,
        IReadOnlyList<InstalledModuleIdentity> modules, bool enabled, CancellationToken token)
    {
        var actions = App.Services?.GetService<ClientActionDispatcher>() ?? new ClientActionDispatcher();
        return frontend.BackendMutations.RunAsync(actions, "client.module.enablement", async cancellationToken =>
        {
            RequireLocalMode(frontend);
            await ConfigureCoreAsync(frontend, root, modules, enabled,
                "client.module.enablement", cancellationToken).ConfigureAwait(false);
        }, token);
    }

    internal static Task InstallAsync(FrontendInstanceService frontend, BackendProcessManager backend,
        GatewayProcessManager? gateway, SharpClawApiClient api, ClientActionDispatcher actions,
        ModulePackageStore store, PreparedModulePackage candidate, CancellationToken cancellationToken) =>
        frontend.BackendMutations.RunAsync(actions, "client.module.install", async token =>
        {
            using var targetGuard = api.HoldTarget(new Uri(backend.ApiUrl));
            RequireLocalMode(frontend);
            BundledBackendProcesses.RequireOwnedTarget(backend, api);
            await store.CommitAsync(candidate,
                Path.Combine(Path.GetDirectoryName(backend.ExecutablePath)!, "contributions"),
                ct => BundledBackendProcesses.StopAsync(backend, gateway, api, ct),
                (root, modules, ct) => ConfigureCoreAsync(frontend, root, modules, true,
                    "client.module.install", ct), token).ConfigureAwait(false);
            candidate.MarkInstalled();
            using var settlement = new CancellationTokenSource(BundledBackendProcesses.StartupTimeout);
            await frontend.BackendMutations.CheckpointAsync("client.module.install",
                BundledBackendMutationStage.Committed, settlement.Token).ConfigureAwait(false);
            await BundledBackendProcesses.StartAsync(backend, gateway, api, settlement.Token).ConfigureAwait(false);
        }, cancellationToken);

    internal static Task SetEnabledAsync(FrontendInstanceService frontend, BackendProcessManager backend,
        GatewayProcessManager? gateway, SharpClawApiClient api, ClientActionDispatcher actions,
        ModulePackageStore store, InstalledModuleIdentity module, bool enabled, CancellationToken cancellationToken) =>
        frontend.BackendMutations.RunAsync(actions, "client.module.enablement", async token =>
        {
            using var targetGuard = api.HoldTarget(new Uri(backend.ApiUrl));
            RequireLocalMode(frontend);
            BundledBackendProcesses.RequireOwnedTarget(backend, api);
            await ConfigureCoreAsync(frontend, store.ActiveRoot, [module], enabled,
                "client.module.enablement", token).ConfigureAwait(false);
            using var settlement = new CancellationTokenSource(BundledBackendProcesses.StartupTimeout);
            await frontend.BackendMutations.CheckpointAsync("client.module.enablement",
                BundledBackendMutationStage.Committed, settlement.Token).ConfigureAwait(false);
            await BundledBackendProcesses.StopAsync(backend, gateway, api, settlement.Token).ConfigureAwait(false);
            if (HasEnabledModules(frontend, backend, store))
                await BundledBackendProcesses.StartAsync(backend, gateway, api, settlement.Token).ConfigureAwait(false);
        }, cancellationToken);

    private static async Task ConfigureCoreAsync(FrontendInstanceService frontend, string root,
        IReadOnlyList<InstalledModuleIdentity> modules, bool enabled, string operation, CancellationToken token)
    {
        var paths = BackendPaths(frontend);
        var store = new SupprocomSecretFileStore(LocalEnvironment.CreateSecretsOptions(
            paths.ConfigDirectory, isDevelopment: false, paths));
        await frontend.BackendMutations.CheckpointAsync(operation,
            BundledBackendMutationStage.BeforeCommit, token).ConfigureAwait(false);
        await store.UpdateDocumentAsync(settings => UpdateSettings(settings, root, modules, enabled), token).ConfigureAwait(false);
    }

    internal static bool HasEnabledModules(FrontendInstanceService frontend, BackendProcessManager backend,
        ModulePackageStore store) => store.ReadInstalled().Concat(ModulePackageStore.ReadIdentities(
            Path.Combine(Path.GetDirectoryName(backend.ExecutablePath)!, "contributions"), true))
        .Any(module => IsEnabled(frontend, module.Id, module.DefaultEnabled));

    internal static void RequireLocalMode(IReadOnlyList<SupprocomSecretSetting> settings)
    {
        foreach (var setting in settings)
        {
            if (string.Equals(setting.Key, "RemoteBackend:Enabled", StringComparison.OrdinalIgnoreCase) &&
                (!bool.TryParse(setting.Value, out var enabled) || enabled))
                throw new InvalidOperationException("Disconnect the remote backend before configuring or activating local modules and providers.");
        }
    }

    internal static IReadOnlyList<SupprocomSecretSetting> UpdateSettings(
        IReadOnlyList<SupprocomSecretSetting> settings, string root, IReadOnlyList<InstalledModuleIdentity> modules, bool enabled)
    {
        RequireLocalMode(settings);
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
