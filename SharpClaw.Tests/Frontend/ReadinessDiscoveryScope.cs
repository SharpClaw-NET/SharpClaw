using System.Text.Json;
using SharpClaw.Services;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Frontend;

internal sealed class ReadinessDiscoveryScope : IAsyncDisposable
{
    internal const string Key = "published-fixture-key";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SharpClaw-readiness-" + Guid.NewGuid().ToString("N"));
    internal FrontendInstanceService Frontend { get; }

    internal ReadinessDiscoveryScope()
    {
        try { Frontend = new FrontendInstanceService(Path.Combine(_root, "frontend"), Path.Combine(_root, "shared"), _root); }
        catch
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            throw;
        }
    }

    internal async Task PublishAsync(Uri target)
    {
        var keyPath = Path.Combine(_root, "api-key");
        await File.WriteAllTextAsync(keyPath, Key, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var directory = Path.Combine(Frontend.Paths.SharedRoot, "discovery", "instances");
        Directory.CreateDirectory(directory);
        var entry = new SharpClawDiscoveryEntry
        {
            InstanceKind = SharpClawInstanceKind.Backend, InstanceId = "readiness-fixture",
            InstallFingerprint = "readiness-fixture", InstanceRoot = _root, BaseUrl = target.ToString(),
            RuntimeDirectory = _root, ApiKeyFilePath = keyPath,
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "backend-readiness-fixture.json"),
            JsonSerializer.Serialize(entry, TestSerializationOptions.CamelCaseIndented),
            TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Frontend.BackendMutations.DisposeAsync().ConfigureAwait(false);
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
