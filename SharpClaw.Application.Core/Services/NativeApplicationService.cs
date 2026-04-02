using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.DTOs.NativeApplications;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// CRUD for <see cref="NativeApplicationDB"/> resources.
/// Native applications are registered executables that agents can
/// launch via the <c>launch_application</c> tool.
/// </summary>
public sealed class NativeApplicationService(SharpClawDbContext db)
{
    public async Task<NativeApplicationResponse> CreateAsync(
        CreateNativeApplicationRequest request, CancellationToken ct = default)
    {
        var app = new NativeApplicationDB
        {
            Name = request.Name,
            ExecutablePath = request.ExecutablePath,
            Alias = request.Alias,
            Description = request.Description,
        };

        db.NativeApplications.Add(app);
        await db.SaveChangesAsync(ct);
        return ToResponse(app);
    }

    public async Task<IReadOnlyList<NativeApplicationResponse>> ListAsync(
        CancellationToken ct = default)
    {
        var apps = await db.NativeApplications
            .OrderBy(a => a.Name)
            .ToListAsync(ct);
        return apps.Select(ToResponse).ToList();
    }

    public async Task<NativeApplicationResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var app = await db.NativeApplications.FindAsync([id], ct);
        return app is null ? null : ToResponse(app);
    }

    public async Task<NativeApplicationResponse?> UpdateAsync(
        Guid id, UpdateNativeApplicationRequest request,
        CancellationToken ct = default)
    {
        var app = await db.NativeApplications.FindAsync([id], ct);
        if (app is null) return null;

        if (request.Name is not null) app.Name = request.Name;
        if (request.ExecutablePath is not null) app.ExecutablePath = request.ExecutablePath;
        if (request.Alias is not null) app.Alias = request.Alias;
        if (request.Description is not null) app.Description = request.Description;

        await db.SaveChangesAsync(ct);
        return ToResponse(app);
    }

    public async Task<bool> DeleteAsync(
        Guid id, CancellationToken ct = default)
    {
        var app = await db.NativeApplications.FindAsync([id], ct);
        if (app is null) return false;

        db.NativeApplications.Remove(app);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Finds a <see cref="NativeApplicationDB"/> by GUID or alias.
    /// Returns <c>null</c> if not found.
    /// </summary>
    public async Task<NativeApplicationDB?> ResolveAsync(
        string idOrAlias, CancellationToken ct = default)
    {
        if (Guid.TryParse(idOrAlias, out var id))
            return await db.NativeApplications.FindAsync([id], ct);

        return await db.NativeApplications
            .FirstOrDefaultAsync(a =>
                a.Alias != null && a.Alias.Equals(idOrAlias, StringComparison.OrdinalIgnoreCase), ct);
    }

    private static NativeApplicationResponse ToResponse(NativeApplicationDB a) => new(
        a.Id, a.Name, a.ExecutablePath, a.Alias,
        a.Description, a.CreatedAt, a.UpdatedAt);
}
