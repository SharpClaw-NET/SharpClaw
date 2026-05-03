namespace SharpClaw.Modules.Providers.LlamaSharp.LocalModels;

public enum LocalModelStatus
{
    Pending = 0,
    Downloading = 1,
    Ready = 2,
    Failed = 3
}
