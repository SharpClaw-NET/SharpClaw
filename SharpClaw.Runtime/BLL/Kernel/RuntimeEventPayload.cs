using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


public sealed record RuntimeEventPayload(
    string Name,
    string SourceId,
    string Summary,
    string? DataJson = null)
{
    public RuntimeEventPayload Validate()
    {
        ValidateText(Name, nameof(Name), 128);
        ValidateText(SourceId, nameof(SourceId), 256);
        ValidateText(Summary, nameof(Summary), 2_048);
        if (DataJson is { Length: > 65_536 })
            throw new ArgumentException(
                "The Runtime event data exceeds the 65536-byte limit.",
                nameof(DataJson));
        return this;
    }

    private static void ValidateText(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ArgumentException(
                $"The Runtime event {name} is empty or exceeds {maxLength} characters.",
                name);
    }
}
