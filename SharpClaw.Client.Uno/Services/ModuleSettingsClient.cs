using System.Text;
using System.Text.Json;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Services;

internal static class ModuleSettingsClient
{
    private static readonly JsonSerializerOptions CatalogJson = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };
    public static async Task<IReadOnlyList<SharpClawModuleSettingsPage>> ReadPagesAsync(
        SharpClawApiClient api, CancellationToken token)
    {
        var json = await ReadAsync(api, "/setup/modules", token).ConfigureAwait(false);
        var pages = JsonSerializer.Deserialize<SharpClawModuleSettingsPage[]>(json, CatalogJson)
            ?? throw new InvalidDataException("Missing module settings catalog.");
        if (pages.Length > 128 || pages.Any(page => page is null ||
            !SharpClawModuleSettings.IsIdentifier(page.SourceId) || !SharpClawModuleSettings.IsIdentifier(page.Id) ||
            !SharpClawModuleSettings.IsLabel(page.ModuleName) || !SharpClawModuleSettings.IsLabel(page.Title) ||
            !SharpClawModuleSettings.IsEndpointPath(page.ReadPath) || !SharpClawModuleSettings.IsEndpointPath(page.SavePath)) ||
            pages.Select(page => (page.SourceId, page.Id)).Distinct().Count() != pages.Length)
            throw new InvalidDataException("Invalid module settings catalog.");
        return pages;
    }

    public static async Task<SharpClawModuleSettingsDocument> ReadDocumentAsync(
        SharpClawApiClient api, SharpClawModuleSettingsPage page, CancellationToken token) =>
        SharpClawModuleSettings.ReadDocument(await ReadAsync(api, page.ReadPath, token).ConfigureAwait(false));

    public static async Task SaveAsync(SharpClawApiClient api, SharpClawModuleSettingsPage page,
        IReadOnlyDictionary<string, object?> values, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(new { values });
        if (Encoding.UTF8.GetByteCount(json) > SharpClawModuleSettings.MaximumDocumentBytes)
            throw new InvalidDataException("Settings update exceeds its limit.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        await api.ConsumeStreamAsync("POST", page.SavePath, content, (response, _) =>
        {
            response.EnsureSuccessStatusCode();
            return Task.CompletedTask;
        }, deadline.Token).ConfigureAwait(false);
    }

    private static async Task<string> ReadAsync(SharpClawApiClient api, string path, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        string? result = null;
        await api.ConsumeStreamAsync("GET", path, null, async (response, ct) =>
        {
            response.EnsureSuccessStatusCode();
            var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var inputAsyncDisposal = input.ConfigureAwait(true);
            using var output = new MemoryStream();
            await ModulePackageSources.CopyBoundedAsync(input, output, SharpClawModuleSettings.MaximumDocumentBytes, ct).ConfigureAwait(false);
            result = Encoding.UTF8.GetString(output.ToArray());
        }, deadline.Token).ConfigureAwait(false);
        return result ?? throw new InvalidDataException("Missing settings response.");
    }
}
