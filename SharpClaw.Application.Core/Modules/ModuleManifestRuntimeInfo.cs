using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Core.Modules;

public sealed record ModuleManifestRuntimeInfo(
    string Runtime,
    string? Entrypoint,
    string? ModuleType = null,
    string? HostMode = null)
{
    public const string DotNet = "dotnet";
    public const string Node = "node";
    public const string Python = "python";
    public const string HostModeInProcess = "in-process";
    public const string HostModeSidecar = "sidecar";
    public static ModuleManifestRuntimeInfo DotNetDefault { get; } = new(DotNet, null);

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 8,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public bool IsDotNet => string.Equals(Runtime, DotNet, StringComparison.Ordinal);
    public bool IsNode => string.Equals(Runtime, Node, StringComparison.Ordinal);
    public bool IsPython => string.Equals(Runtime, Python, StringComparison.Ordinal);
    public bool IsScriptRuntime => IsNode || IsPython;
    public bool IsSidecarHostMode => string.Equals(HostMode, HostModeSidecar, StringComparison.Ordinal);
    public bool IsInProcessHostMode => string.Equals(HostMode, HostModeInProcess, StringComparison.Ordinal);

    public static ModuleManifestRuntimeInfo FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json, DocumentOptions);
        var root = doc.RootElement;
        var runtime = TryGetString(root, "runtime");
        var entrypoint = TryGetString(root, "entrypoint");
        var moduleType = TryGetString(root, "moduleType")
            ?? TryGetString(root, "type");
        var hostMode = NormalizeHostMode(TryGetString(root, "hostMode"));

        return new ModuleManifestRuntimeInfo(Normalize(runtime), entrypoint, moduleType, hostMode);
    }

    public static string Normalize(string? runtime) =>
        string.IsNullOrWhiteSpace(runtime)
            ? DotNet
            : runtime.Trim().ToLowerInvariant();

    public static string? NormalizeHostMode(string? hostMode)
    {
        if (string.IsNullOrWhiteSpace(hostMode))
            return null;

        var normalized = hostMode.Trim().ToLowerInvariant();
        return normalized is "inprocess" or "in-process"
            ? HostModeInProcess
            : normalized;
    }

    public void EnsureDotNetEntryAssembly(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!IsDotNet)
        {
            throw new NotSupportedException(
                $"Module '{manifest.Id}' declares runtime '{Runtime}', but this SharpClaw build only supports " +
                $"'{DotNet}' modules. JavaScript and Python sidecar runtimes are not implemented yet.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            throw new InvalidOperationException(
                $"Module '{manifest.Id}' declares runtime '{DotNet}' but has no entryAssembly.");
        }

        PathGuard.EnsureFileName(manifest.EntryAssembly, nameof(manifest.EntryAssembly));
        PathGuard.EnsureExtension(manifest.EntryAssembly, ".dll");

        if (!string.IsNullOrWhiteSpace(ModuleType)
            && ModuleType.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Module '{manifest.Id}' declares an invalid moduleType.");
        }
    }

    public void EnsureScriptEntrypoint(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!IsScriptRuntime)
        {
            throw new NotSupportedException(
                $"Module '{manifest.Id}' declares runtime '{Runtime}', but script entrypoints are only supported for " +
                $"'{Node}' and '{Python}' modules.");
        }

        if (string.IsNullOrWhiteSpace(Entrypoint))
        {
            throw new InvalidOperationException(
                $"Module '{manifest.Id}' declares runtime '{Runtime}' but has no entrypoint.");
        }

        if (Entrypoint.Any(char.IsControl) || Path.IsPathRooted(Entrypoint))
        {
            throw new InvalidOperationException(
                $"Module '{manifest.Id}' declares an invalid script entrypoint.");
        }

        if (!string.IsNullOrWhiteSpace(ModuleType)
            && ModuleType.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Module '{manifest.Id}' declares an invalid moduleType.");
        }
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }
}
