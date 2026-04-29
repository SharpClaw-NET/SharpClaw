using Microsoft.EntityFrameworkCore;

namespace SharpClaw.Modules.SystemAudio.Services;

/// <summary>
/// Resolves input audio device details from <see cref="SystemAudioDbContext"/>.
/// </summary>
public sealed class InputAudioDeviceResolver(SystemAudioDbContext db) : IInputAudioDeviceResolver
{
    public async Task<(string DeviceIdentifier, string Name)?> GetDeviceAsync(
        Guid id, CancellationToken ct = default)
    {
        var device = await db.InputAudios
            .Where(d => d.Id == id)
            .Select(d => new { d.DeviceIdentifier, d.Name })
            .FirstOrDefaultAsync(ct);

        if (device is null || device.DeviceIdentifier is null)
            return null;

        return (device.DeviceIdentifier, device.Name);
    }

    public async Task<Guid> GetDefaultDeviceIdAsync(CancellationToken ct = default)
    {
        var device = await db.InputAudios
            .OrderBy(d => d.CreatedAt)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(ct);

        return device ?? Guid.Empty;
    }
}
