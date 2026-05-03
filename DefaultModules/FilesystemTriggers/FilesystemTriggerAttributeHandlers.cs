using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.FilesystemTriggers;

/// <summary>
/// Module-owned <see cref="ITaskTriggerAttributeHandler"/> for
/// <c>[OnFileChanged]</c>. Behavior preserved verbatim from the legacy
/// core parser switch.
/// </summary>
internal static class FilesystemTriggerAttributeHandlers
{
    public static IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> All { get; } =
        new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal)
        {
            ["OnFileChanged"] = new OnFileChangedHandler(),
        };

    private sealed class OnFileChangedHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var watchPath = context.GetStringArg(0);
            if (!string.IsNullOrEmpty(watchPath))
                p[FilesystemTriggerKeys.WatchPath] = watchPath;
            var pattern = context.GetNamedStringArg("Pattern");
            if (!string.IsNullOrEmpty(pattern))
                p[FilesystemTriggerKeys.FilePattern] = pattern;
            var events = context.GetNamedEnumArg<FileWatchEvent>("Events") ?? FileWatchEvent.Any;
            if (events != default)
                p[FilesystemTriggerKeys.FileEvents] = events.ToString();
            return new TaskTriggerDefinition
            {
                TriggerKey = FilesystemTriggerKeys.FileChanged,
                Parameters = p,
            };
        }
    }
}
