namespace SharpClaw.Modules.FilesystemTriggers;

/// <summary>
/// File-system events that the <c>[OnFileChanged]</c> trigger watches for.
/// </summary>
[Flags]
public enum FileWatchEvent
{
    Created = 1,
    Changed = 2,
    Deleted = 4,
    Renamed = 8,
    Any     = Created | Changed | Deleted | Renamed,
}
