using Microsoft.EntityFrameworkCore;

using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Modules.Transcription.Clients;

namespace SharpClaw.Modules.Transcription.Services;

/// <summary>
/// Manages input audio CRUD. Transcription job lifecycle is handled
/// by <see cref="SharpClaw.Application.Services.AgentJobService"/> via the job/permission system.
/// </summary>
public sealed class TranscriptionService(SharpClawDbContext db, IAudioCaptureProvider capture)
{
    // ═══════════════════════════════════════════════════════════════
    // Input audio CRUD
    // ═══════════════════════════════════════════════════════════════

    public async Task<InputAudioResponse> CreateDeviceAsync(CreateInputAudioRequest request, CancellationToken ct = default)
    {
        var device = new InputAudioDB
        {
            Name = request.Name,
            DeviceIdentifier = request.DeviceIdentifier,
            Description = request.Description
        };

        db.InputAudios.Add(device);
        await db.SaveChangesAsync(ct);

        return ToResponse(device);
    }

    public async Task<IReadOnlyList<InputAudioResponse>> ListDevicesAsync(CancellationToken ct = default)
    {
        var devices = await db.InputAudios
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        return devices.Select(ToResponse).ToList();
    }

    public async Task<InputAudioResponse?> GetDeviceByIdAsync(Guid id, CancellationToken ct = default)
    {
        var device = await db.InputAudios.FirstOrDefaultAsync(d => d.Id == id, ct);
        return device is not null ? ToResponse(device) : null;
    }

    public async Task<InputAudioResponse?> UpdateDeviceAsync(Guid id, UpdateInputAudioRequest request, CancellationToken ct = default)
    {
        var device = await db.InputAudios.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return null;

        if (request.Name is not null) device.Name = request.Name;
        if (request.DeviceIdentifier is not null) device.DeviceIdentifier = request.DeviceIdentifier;
        if (request.Description is not null) device.Description = request.Description;

        await db.SaveChangesAsync(ct);
        return ToResponse(device);
    }

    public async Task<bool> DeleteDeviceAsync(Guid id, CancellationToken ct = default)
    {
        var device = await db.InputAudios.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return false;

        db.InputAudios.Remove(device);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // Sync — discover system audio devices and import new ones
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Discovers all audio input devices on the current system and
    /// imports any that are not already in the database. Duplicates
    /// (matched by <see cref="InputAudioDB.DeviceIdentifier"/>) are
    /// skipped.
    /// </summary>
    public async Task<InputAudioSyncResult> SyncDevicesAsync(CancellationToken ct = default)
    {
        var systemDevices = capture.ListDevices();

        var existingIdentifiers = await db.InputAudios
            .Where(d => d.DeviceIdentifier != null)
            .Select(d => d.DeviceIdentifier!)
            .ToListAsync(ct);

        var existingSet = new HashSet<string>(
            existingIdentifiers, StringComparer.OrdinalIgnoreCase);

        var imported = new List<string>();
        var skipped = new List<string>();

        foreach (var (id, name) in systemDevices)
        {
            if (existingSet.Contains(id))
            {
                skipped.Add(name);
                continue;
            }

            var device = new InputAudioDB
            {
                Name = name,
                DeviceIdentifier = id,
                Description = "Synced from system audio devices",
            };

            db.InputAudios.Add(device);
            imported.Add(name);
        }

        if (imported.Count > 0)
            await db.SaveChangesAsync(ct);

        return new InputAudioSyncResult(
            imported.Count,
            skipped.Count,
            imported,
            skipped);
    }

    private static InputAudioResponse ToResponse(InputAudioDB d) =>
        new(d.Id, d.Name, d.DeviceIdentifier, d.Description, d.SkillId, d.CreatedAt);
}
