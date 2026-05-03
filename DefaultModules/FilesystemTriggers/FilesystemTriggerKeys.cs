namespace SharpClaw.Modules.FilesystemTriggers;

/// <summary>
/// Trigger and parameter keys owned by the filesystem-triggers module.
/// String values are persisted verbatim in binding rows and serialized
/// scripts.
/// </summary>
public static class FilesystemTriggerKeys
{
    /// <summary>Trigger-key value persisted in <c>TaskTriggerBindingDB.Kind</c>.</summary>
    public const string FileChanged = "FileChanged";

    // Parameter names — must match TaskTriggerDefinition property names.
    public const string WatchPath   = "WatchPath";
    public const string FilePattern = "FilePattern";
    public const string FileEvents  = "FileEvents";
}
