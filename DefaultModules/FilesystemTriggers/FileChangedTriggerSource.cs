using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.FilesystemTriggers;

/// <summary>
/// Trigger source that fires when files under a watched path are created,
/// changed, deleted, or renamed via <see cref="FileSystemWatcher"/>.
/// <para>
/// Moved out of <c>SharpClaw.Application.Core</c> by the trigger-extraction
/// plan; behavior is preserved verbatim. The persisted trigger-key value
/// (<see cref="FilesystemTriggerKeys.FileChanged"/>) remains
/// <c>"FileChanged"</c> so existing binding rows continue to round-trip.
/// </para>
/// </summary>
public sealed class FileChangedTriggerSource(
    ILogger<FileChangedTriggerSource> logger) : ITaskTriggerSource, IAsyncDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];

    public string TriggerKey => FilesystemTriggerKeys.FileChanged;

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        StopWatchers();
        _contexts = contexts;

        foreach (var ctx in contexts)
        {
            var path = ctx.Definition.Parameters.GetValueOrDefault(FilesystemTriggerKeys.WatchPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                logger.LogWarning(
                    "FileChangedTriggerSource: definition {Id} has no WatchPath; skipping.",
                    ctx.TaskDefinitionId);
                continue;
            }

            var dir     = Path.GetDirectoryName(path) ?? path;
            var pattern = ctx.Definition.Parameters.GetValueOrDefault(FilesystemTriggerKeys.FilePattern) ?? "*";

            if (!Directory.Exists(dir))
            {
                logger.LogWarning(
                    "FileChangedTriggerSource: watch directory '{Dir}' does not exist; skipping.", dir);
                continue;
            }

            var watcher = new FileSystemWatcher(dir, pattern)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents   = false,
            };

            var rawEvents = ctx.Definition.Parameters.GetValueOrDefault(FilesystemTriggerKeys.FileEvents);
            var events = ParseFileEvents(rawEvents);
            if (events == 0) events = FileWatchEvent.Any;

            if (events.HasFlag(FileWatchEvent.Created)) watcher.Created += (_, _) => Fire(ctx);
            if (events.HasFlag(FileWatchEvent.Changed)) watcher.Changed += (_, _) => Fire(ctx);
            if (events.HasFlag(FileWatchEvent.Deleted)) watcher.Deleted += (_, _) => Fire(ctx);
            if (events.HasFlag(FileWatchEvent.Renamed)) watcher.Renamed += (_, _) => Fire(ctx);

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopWatchers();
        _contexts = [];
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public string? GetBindingValue(TaskTriggerDefinition def) =>
        def.TriggerKey == FilesystemTriggerKeys.FileChanged
            ? def.Parameters.GetValueOrDefault(FilesystemTriggerKeys.WatchPath)
            : null;

    public ValueTask DisposeAsync()
    {
        StopWatchers();
        return ValueTask.CompletedTask;
    }

    private void StopWatchers()
    {
        foreach (var w in _watchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FileSystemWatcher dispose failed.");
            }
        }

        _watchers.Clear();
    }

    private void Fire(ITaskTriggerSourceContext ctx) =>
        _ = Task.Run(async () =>
        {
            try { await ctx.FireAsync(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "FileChangedTriggerSource failed to fire context for definition {Id}.",
                    ctx.TaskDefinitionId);
            }
        });

    private static FileWatchEvent ParseFileEvents(string? raw) =>
        Enum.TryParse<FileWatchEvent>(raw, ignoreCase: true, out var parsed) ? parsed : 0;
}
