namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Per-endpoint enable/disable toggles for the public gateway.
/// Loaded from the <c>Gateway:Endpoints</c> configuration section.
/// When <see cref="Enabled"/> is <c>false</c>, the entire gateway
/// returns <c>503 Service Unavailable</c> for every request.
/// Individual endpoint groups can be toggled independently.
/// <para>
/// Default posture: only <see cref="Bots"/> is enabled so the gateway
/// acts purely as a bot relay.  Enable individual groups as needed to
/// expose the public REST surface.
/// </para>
/// </summary>
public sealed class GatewayEndpointOptions
{
    public const string SectionName = "Gateway:Endpoints";

    /// <summary>Master kill-switch for the entire public gateway.</summary>
    public bool Enabled { get; set; } = true;

    public bool Auth { get; set; }
    public bool Agents { get; set; }
    public bool Channels { get; set; }
    public bool ChannelContexts { get; set; }
    public bool Chat { get; set; }
    public bool ChatStream { get; set; }
    public bool Threads { get; set; }
    public bool ThreadChat { get; set; }
    public bool Jobs { get; set; }
    public bool Models { get; set; }
    public bool Providers { get; set; }
    public bool Roles { get; set; }
    public bool Users { get; set; }
    public bool InputAudios { get; set; }
    public bool Transcription { get; set; }
    public bool TranscriptionStreaming { get; set; }
    public bool Cost { get; set; }
    public bool Bots { get; set; } = true;

    /// <summary>
    /// Resolves whether a given endpoint group is active.
    /// Returns <c>false</c> if the master switch is off or the
    /// specific group is disabled.
    /// </summary>
    public bool IsEndpointEnabled(string groupName)
    {
        if (!Enabled) return false;

        return groupName switch
        {
            nameof(Auth) => Auth,
            nameof(Agents) => Agents,
            nameof(Channels) => Channels,
            nameof(ChannelContexts) => ChannelContexts,
            nameof(Chat) => Chat,
            nameof(ChatStream) => ChatStream,
            nameof(Threads) => Threads,
            nameof(ThreadChat) => ThreadChat,
            nameof(Jobs) => Jobs,
            nameof(Models) => Models,
            nameof(Providers) => Providers,
            nameof(Roles) => Roles,
            nameof(Users) => Users,
            nameof(InputAudios) => InputAudios,
            nameof(Transcription) => Transcription,
            nameof(TranscriptionStreaming) => TranscriptionStreaming,
            nameof(Cost) => Cost,
            nameof(Bots) => Bots,
            _ => true
        };
    }
}
