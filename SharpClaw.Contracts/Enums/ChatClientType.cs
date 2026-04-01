namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Identifies the client interface that originated a chat message.
/// Used in the chat header so the agent knows the communication channel.
/// </summary>
public enum ChatClientType
{
    CLI = 0,
    API = 1,
    Telegram = 2,
    Discord = 3,
    WhatsApp = 4,
    VisualStudio = 5,
    VisualStudioCode = 6,
    UnoWindows = 7,
    UnoAndroid = 8,
    UnoMacOS = 9,
    UnoLinux = 10,
    UnoBrowser = 11,
    Other = 12,
    Slack = 13,
    Matrix = 14,
    Signal = 15,
    Email = 16,
    Teams = 17,
}
