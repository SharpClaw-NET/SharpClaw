using System.Text.Json;

namespace SharpClaw.Services;

/// <summary>
/// Thin client-side helper that delegates Core <c>.env</c> authorisation
/// to the server-side <c>/env/core/auth</c> endpoint.
/// <para>
/// All real security (admin check, <c>AllowNonAdmin</c> override, DB
/// lookup) lives in <c>EnvFileService</c> on the API.  This class is
/// purely a convenience so the UI can pre-check access before navigating
/// to the editor and show a meaningful error on denial.
/// </para>
/// </summary>
public static class CoreEnvGuard
{
    /// <summary>
    /// Returns <c>true</c> when the server confirms the current user is
    /// allowed to read/write the Core <c>.env</c> file.
    /// </summary>
    public static async Task<bool> IsAuthorisedAsync(IServiceProvider services)
    {
        try
        {
            var api = services.GetRequiredService<SharpClawApiClient>();

            // No token → not authenticated at all.
            if (api.AccessToken is null)
                return false;

            using var resp = await api.GetAsync("/env/core/auth");
            if (!resp.IsSuccessStatusCode)
                return false;

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.TryGetProperty("authorised", out var prop)
                   && prop.GetBoolean();
        }
        catch
        {
            return false;
        }
    }
}
