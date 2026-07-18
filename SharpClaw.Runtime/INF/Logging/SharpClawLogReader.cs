using SharpClaw.Contracts.DTOs.Diagnostics;
using SharpClaw.Shared.DurableStorage;
using SharpClaw.Runtime.INF.DurableStorage;
using SharpClaw.Shared.Logging;

namespace SharpClaw.Runtime.INF.Logging;

/// <summary>
/// Reads bounded operational process and module streams through the existing
/// authenticated cursor facade. Job, task-log, task-output, and artifact
/// operations remain owned by <see cref="ExecutionDiagnosticStore"/>.
/// </summary>
public sealed class SharpClawLogReader(
    ExecutionDiagnosticStore diagnostics,
    SharpClawLogRuntime runtime)
{
    public string AppName => runtime.AppName;
    public Guid BootId => runtime.BootId;

    public Task<DurableOperationalStreamCatalog> EnumerateOperationalStreamsAsync(
        DurableOperationalStreamEnumerationOptions options,
        CancellationToken cancellationToken = default) =>
        runtime.EnumerateOperationalStreamsAsync(options, cancellationToken);

    public ValueTask<DurableLogPageResponse> ReadProcessLogsAsync(
        string? cursor,
        DurableLogQuery query,
        CancellationToken cancellationToken = default) =>
        diagnostics.ReadProcessLogsAsync(
            runtime.AppName,
            runtime.BootId,
            cursor,
            query,
            cancellationToken);

    public ValueTask<DurableLogPageResponse> ReadProcessLogsAsync(
        string appName,
        Guid bootId,
        string? cursor,
        DurableLogQuery query,
        CancellationToken cancellationToken = default) =>
        diagnostics.ReadProcessLogsAsync(
            appName,
            bootId,
            cursor,
            query,
            cancellationToken);

    public ValueTask<DurableLogPageResponse> ReadModuleLogsAsync(
        string moduleId,
        Guid bootId,
        string? cursor,
        DurableLogQuery query,
        CancellationToken cancellationToken = default) =>
        diagnostics.ReadOperationalModuleLogsAsync(
            moduleId,
            bootId,
            cursor,
            query,
            cancellationToken);
}
