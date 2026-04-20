using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

/// <summary>
/// Server-side service that manages reading and writing the Core
/// <c>Environment/.env</c> file with authorisation enforcement.
/// <para>
/// Authorisation rules:
/// <list type="bullet">
///   <item>Caller must be authenticated (valid <see cref="SessionService.UserId"/>).</item>
///   <item>If <c>EnvEditor:AllowNonAdmin</c> is <c>true</c> in the
///         current configuration, any authenticated user is allowed.</item>
///   <item>Otherwise, only users with <c>IsUserAdmin == true</c> may
///         read or write the file.</item>
/// </list>
/// </para>
/// </summary>
public sealed class EnvFileService(
    SharpClawDbContext db,
    SessionService session,
    IConfiguration configuration)
{
    /// <summary>
    /// Returns <c>true</c> when the current session user is authorised
    /// to read or write the Core <c>.env</c> file.
    /// </summary>
    public async Task<bool> IsAuthorisedAsync(CancellationToken ct = default)
    {
        if (session.UserId is not { } userId)
            return false;

        // Fast path: configuration allows non-admin editing.
        if (IsNonAdminEditingAllowed())
            return true;

        // Default: require admin.
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is { IsUserAdmin: true };
    }

    /// <summary>
    /// Reads the Core <c>.env</c> file and returns its raw content.
    /// Decrypts transparently if the file is encrypted on disk.
    /// Returns <c>null</c> when the file does not exist.
    /// Throws <see cref="UnauthorizedAccessException"/> on auth failure.
    /// </summary>
    public async Task<string?> ReadAsync(CancellationToken ct = default)
    {
        if (!await IsAuthorisedAsync(ct))
            throw new UnauthorizedAccessException("Admin login required to read Application Core environment.");

        var path = ResolveEnvFilePath();
        if (!File.Exists(path)) return null;

        var key = EncryptionKeyResolver.ResolveKey();
        return await EncryptedEnvFile.ReadAsync(path, key, ct);
    }

    /// <summary>
    /// Writes the given content to the Core <c>.env</c> file.
    /// Always writes encrypted when an encryption key is available.
    /// Creates the directory if it does not exist.
    /// Throws <see cref="UnauthorizedAccessException"/> on auth failure.
    /// </summary>
    public async Task WriteAsync(string content, CancellationToken ct = default)
    {
        if (!await IsAuthorisedAsync(ct))
            throw new UnauthorizedAccessException("Admin login required to edit Application Core environment.");

        var path = ResolveEnvFilePath();
        var key = EncryptionKeyResolver.ResolveKey();
        // Always write encrypted — auto-lock ensures the file is encrypted
        // after first startup, and all subsequent writes preserve that.
        await EncryptedEnvFile.WriteAsync(path, content, key, encrypt: true, ct);
    }

    // ── Internals ──────────────────────────────────────────────────

    private bool IsNonAdminEditingAllowed()
    {
        var value = configuration["EnvEditor:AllowNonAdmin"];
        return bool.TryParse(value, out var allowed) && allowed;
    }

    private static string ResolveEnvFilePath()
    {
        return Path.Combine(
            Path.GetDirectoryName(typeof(EnvFileService).Assembly.Location)!,
            "Environment", ".env");
    }
}
