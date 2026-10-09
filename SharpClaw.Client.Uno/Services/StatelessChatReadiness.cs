using System.Text;
using System.Text.Json;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Services;

/// <summary>Debug chat never infers provider availability from a failed setup probe.</summary>
internal static class StatelessChatReadiness
{
    private static readonly JsonSerializerOptions MetadataJson = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };
    public const string RequiredMessage =
        "Go back to Settings and select a model from an available provider. Install a provider module if none is available.";

    public static bool CanChat(SharpClawProviderSetup? setup) =>
        setup is { SetupRequired: false, Providers: not null } &&
        !string.IsNullOrWhiteSpace(setup.ProviderKey) &&
        !string.IsNullOrWhiteSpace(setup.Model) &&
        setup.Providers.Count(provider => string.Equals(
            provider?.Key, setup.ProviderKey, StringComparison.OrdinalIgnoreCase)) == 1;

    public static bool CanChat(SharpClawProviderSetup? setup, SharpClawProviderModels? catalog) =>
        CanChat(setup) && catalog is { Models: not null } &&
        string.Equals(catalog.ProviderKey, setup!.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
        catalog.Models.Contains(setup.Model, StringComparer.Ordinal);

    public static async Task<bool> CheckAsync(
        SharpClawApiClient api, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var setup = await ReadAsync<SharpClawProviderSetup>(api, "/setup/provider", deadline.Token).ConfigureAwait(false);
            if (!CanChat(setup)) return false;
            return CanChat(setup, await ReadAsync<SharpClawProviderModels>(api, "/setup/models", deadline.Token).ConfigureAwait(false));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    internal static async Task<T?> ReadAsync<T>(SharpClawApiClient api, string path, CancellationToken token)
    {
        T? result = default;
        await api.ConsumeStreamAsync("GET", path, null, async (response, ct) =>
        {
            response.EnsureSuccessStatusCode();
            var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var inputAsyncDisposal = input.ConfigureAwait(false);
            using var output = new MemoryStream();
            await ModulePackageSources.CopyBoundedAsync(input, output, 1024 * 1024, ct).ConfigureAwait(false);
            result = JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(output.ToArray()), MetadataJson);
        }, token).ConfigureAwait(false);
        return result;
    }
}
