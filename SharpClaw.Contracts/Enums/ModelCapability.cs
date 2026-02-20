namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Capabilities that a model can advertise. A model may support
/// multiple capabilities simultaneously (e.g. Chat + Transcription).
/// </summary>
[Flags]
public enum ModelCapability
{
    None = 0,
    Chat = 1,
    Transcription = 2,
    ImageGeneration = 4,
    Embedding = 8,
    TextToSpeech = 16
}
