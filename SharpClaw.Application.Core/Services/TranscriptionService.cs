using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages audio device CRUD. Transcription job lifecycle is handled
/// by <see cref="AgentJobService"/> via the job/permission system.
/// </summary>
public sealed class TranscriptionService(SharpClawDbContext db)
{
    // ═══════════════════════════════════════════════════════════════
    // Audio device CRUD
    // ═══════════════════════════════════════════════════════════════

    public async Task<AudioDeviceResponse> CreateDeviceAsync(CreateAudioDeviceRequest request, CancellationToken ct = default)
    {
        var device = new AudioDeviceDB
        {
            Name = request.Name,
            DeviceIdentifier = request.DeviceIdentifier,
            Description = request.Description
        };

        db.AudioDevices.Add(device);
        await db.SaveChangesAsync(ct);

        return ToResponse(device);
    }

    public async Task<IReadOnlyList<AudioDeviceResponse>> ListDevicesAsync(CancellationToken ct = default)
    {
        var devices = await db.AudioDevices
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        return devices.Select(ToResponse).ToList();
    }

    public async Task<AudioDeviceResponse?> GetDeviceByIdAsync(Guid id, CancellationToken ct = default)
    {
        var device = await db.AudioDevices.FirstOrDefaultAsync(d => d.Id == id, ct);
        return device is not null ? ToResponse(device) : null;
    }

    public async Task<AudioDeviceResponse?> UpdateDeviceAsync(Guid id, UpdateAudioDeviceRequest request, CancellationToken ct = default)
    {
        var device = await db.AudioDevices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return null;

        if (request.Name is not null) device.Name = request.Name;
        if (request.DeviceIdentifier is not null) device.DeviceIdentifier = request.DeviceIdentifier;
        if (request.Description is not null) device.Description = request.Description;

        await db.SaveChangesAsync(ct);
        return ToResponse(device);
    }

    public async Task<bool> DeleteDeviceAsync(Guid id, CancellationToken ct = default)
    {
        var device = await db.AudioDevices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return false;

        db.AudioDevices.Remove(device);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static AudioDeviceResponse ToResponse(AudioDeviceDB d) =>
        new(d.Id, d.Name, d.DeviceIdentifier, d.Description, d.SkillId, d.CreatedAt);
}
