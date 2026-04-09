using Microsoft.EntityFrameworkCore;
using Mk8.Shell.Models;
using Mk8.Shell.Startup;
using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.DTOs.Containers;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages container CRUD, mk8.shell sandbox provisioning via
/// <see cref="Mk8SandboxRegistrar"/>, and local sync from
/// <c>%APPDATA%/mk8.shell/sandboxes.json</c>.
/// </summary>
public sealed class ContainerService(SharpClawDbContext db, SessionService session)
{
    // ═══════════════════════════════════════════════════════════════
    // Create
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a container. For <see cref="ContainerType.Mk8Shell"/>,
    /// provisions a sandbox at <c>{path}/{name}/</c> via mk8.shell.startup
    /// before saving to the database.
    /// </summary>
    public async Task<ContainerResponse> CreateAsync(
        CreateContainerRequest request, CancellationToken ct = default)
    {
        return request.Type switch
        {
            ContainerType.Mk8Shell => await CreateMk8ShellAsync(request, ct),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request), $"Unsupported container type: {request.Type}"),
        };
    }

    private async Task<ContainerResponse> CreateMk8ShellAsync(
        CreateContainerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException(
                "Path is required for mk8shell containers. " +
                "Specify the parent directory where the sandbox folder " +
                "will be created.");

        var sandboxName = request.Name;
        var sandboxDir = Path.Combine(
            Path.GetFullPath(request.Path), sandboxName);

        // Check for duplicate in database.
        var exists = await db.Containers.AnyAsync(
            c => c.Type == ContainerType.Mk8Shell
                 && c.SandboxName == sandboxName, ct);

        if (exists)
            throw new InvalidOperationException(
                $"An mk8shell container with sandbox name '{sandboxName}' " +
                "already exists in the database.");

        // Provision sandbox via mk8.shell.startup.
        // This validates the name (English letters + digits only),
        // creates the directory, writes .env + .signed.env, registers
        // in local %APPDATA%/mk8.shell, and starts the OS-level
        // container immediately — the sandbox is containerized from
        // this point forward.
        await Mk8SandboxRegistrar.RegisterAsync(sandboxName, sandboxDir, ct: ct);

        // Save to SharpClaw database.
        var container = new ContainerDB
        {
            Name = $"mk8shell:{sandboxName}",
            Type = ContainerType.Mk8Shell,
            SandboxName = sandboxName,
            Description = request.Description,
        };

        db.Containers.Add(container);
        await db.SaveChangesAsync(ct);

        // Create an owner role for this container. The role's permission
        // set grants Independent clearance for this specific container
        // and nothing else. Assigned to the creating user.
        await CreateOwnerRoleAsync(container, ct);

        return ToResponse(container);
    }

    /// <summary>
    /// Creates a "<c>[container name] Owner</c>" role with a permission set
    /// granting <see cref="PermissionClearance.Independent"/> clearance for
    /// the specific container and nothing else. Assigns the role to the
    /// current session user.
    /// </summary>
    private async Task CreateOwnerRoleAsync(
        ContainerDB container, CancellationToken ct)
    {
        var permissionSet = new PermissionSetDB
        {
            ResourceAccesses =
            [
                new ResourceAccessDB
                {
                    ResourceType = ResourceTypes.Container,
                    ResourceId = container.Id,
                    Clearance = PermissionClearance.Independent,
                },
                new ResourceAccessDB
                {
                    ResourceType = ResourceTypes.Mk8Shell,
                    ResourceId = container.Id,
                    Clearance = PermissionClearance.Independent,
                    SubType = SafeShellType.Mk8Shell.ToString(),
                },
            ],
        };

        db.PermissionSets.Add(permissionSet);
        await db.SaveChangesAsync(ct);

        var role = new RoleDB
        {
            Name = $"{container.Name} Owner",
            PermissionSetId = permissionSet.Id,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);

        // Assign the role to the creating user if they don't already
        // have one. Users with an existing role (e.g. Admin) already
        // have access through their current permission set — overwriting
        // would downgrade them.
        if (session.UserId is { } userId)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is not null && user.RoleId is null)
            {
                user.RoleId = role.Id;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Read
    // ═══════════════════════════════════════════════════════════════

    public async Task<ContainerResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var container = await db.Containers
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        return container is not null ? ToResponse(container) : null;
    }

    public async Task<IReadOnlyList<ContainerResponse>> ListAsync(
        CancellationToken ct = default)
    {
        var containers = await db.Containers
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        return containers.Select(ToResponse).ToList();
    }

    public async Task<ContainerResponse?> GetBySandboxNameAsync(
        string sandboxName, CancellationToken ct = default)
    {
        var container = await db.Containers
            .FirstOrDefaultAsync(
                c => c.Type == ContainerType.Mk8Shell && c.SandboxName == sandboxName, ct);
        return container is not null ? ToResponse(container) : null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Update
    // ═══════════════════════════════════════════════════════════════

    public async Task<ContainerResponse?> UpdateAsync(
        Guid id, UpdateContainerRequest request, CancellationToken ct = default)
    {
        var container = await db.Containers
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (container is null) return null;

        if (request.Name is not null) container.Name = request.Name;
        if (request.Description is not null) container.Description = request.Description;

        await db.SaveChangesAsync(ct);
        return ToResponse(container);
    }

    // ═══════════════════════════════════════════════════════════════
    // Delete
    // ═══════════════════════════════════════════════════════════════

    public async Task<bool> DeleteAsync(
        Guid id, CancellationToken ct = default)
    {
        var container = await db.Containers
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (container is null) return false;

        db.Containers.Remove(container);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // Sync — import local mk8.shell sandboxes into the database
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads all sandbox entries from the local
    /// <c>%APPDATA%/mk8.shell/sandboxes.json</c> registry and imports
    /// any that are not already in the SharpClaw database. Duplicates
    /// (matched by <see cref="ContainerDB.SandboxName"/>) are skipped.
    /// </summary>
    public async Task<ContainerSyncResult> SyncLocalMk8ShellAsync(
        CancellationToken ct = default)
    {
        var registry = new Mk8SandboxRegistry();

        Dictionary<string, Mk8SandboxEntry> localSandboxes;
        try
        {
            localSandboxes = registry.LoadSandboxes();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to read local mk8.shell registry. " +
                "Ensure mk8.shell is initialized on this machine.", ex);
        }

        // Get existing mk8shell sandbox names in the database.
        var existingNames = await db.Containers
            .Where(c => c.Type == ContainerType.Mk8Shell
                        && c.SandboxName != null)
            .Select(c => c.SandboxName!)
            .ToListAsync(ct);

        var existingSet = new HashSet<string>(
            existingNames, StringComparer.OrdinalIgnoreCase);

        var imported = new List<string>();
        var skipped = new List<string>();

        foreach (var (name, entry) in localSandboxes)
        {
            if (existingSet.Contains(name))
            {
                skipped.Add(name);
                continue;
            }

            var container = new ContainerDB
            {
                Name = $"mk8shell:{name}",
                Type = ContainerType.Mk8Shell,
                SandboxName = name,
                Description = $"Synced from local registry at {entry.RootPath}",
            };

            db.Containers.Add(container);
            imported.Add(name);
        }

        if (imported.Count > 0)
            await db.SaveChangesAsync(ct);

        return new ContainerSyncResult(
            imported.Count,
            skipped.Count,
            imported,
            skipped);
    }

    // ═══════════════════════════════════════════════════════════════
    // Mapping
    // ═══════════════════════════════════════════════════════════════

    private static ContainerResponse ToResponse(ContainerDB c)
    {
        string? localPath = null;

        // For mk8shell containers, try to resolve the local path.
        if (c.Type == ContainerType.Mk8Shell
            && !string.IsNullOrEmpty(c.SandboxName))
        {
            try
            {
                var registry = new Mk8SandboxRegistry();
                var entry = registry.Resolve(c.SandboxName);
                localPath = entry.RootPath;
            }
            catch
            {
                // Not registered on this machine — that's fine.
            }
        }

        return new ContainerResponse(
            c.Id,
            c.Type,
            c.Name,
            c.SandboxName,
            localPath,
            c.Description,
            c.CreatedAt);
    }
}
