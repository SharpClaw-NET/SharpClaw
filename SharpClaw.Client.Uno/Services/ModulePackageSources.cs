using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Services;

/// <summary>Anonymous by default. Explicit package credentials never reach Runtime or redirects.</summary>
internal sealed class ModulePackageSources : IDisposable
{
    private readonly HttpClient _http;
    public ModulePackageSources() : this(CreateHttpClient()) { }
    internal ModulePackageSources(HttpClient http) => _http = http;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "The returned HttpClient owns its handler through disposeHandler: true. Every constructor/configuration failure disposes the acquired handler/client before rethrowing; the analyzer cannot follow this explicit ownership transfer.")]
    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true };
        try { return new HttpClient(handler, disposeHandler: true); }
        catch { handler.Dispose(); throw; }
    }

    public async Task<IReadOnlyList<ModulePackageSource>> ResolveAsync(
        string source, string? githubToken, CancellationToken cancellationToken)
    {
        source = source.Trim().Trim('"');
        if (Path.IsPathFullyQualified(source) && (File.Exists(source) || Directory.Exists(source)))
            return [new(Path.GetFileName(source), LocalPath: Path.GetFullPath(source))];
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            throw new InvalidDataException("Use an existing absolute local path, NuGet package link or GitHub release/package link.");
        RequireDownloadUri(uri);
        if (!string.IsNullOrEmpty(uri.Query)) throw new InvalidDataException("Use a canonical package/release link without query parameters.");
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        if (uri.Host.Equals("www.nuget.org", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("nuget.org", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length is 2 or 3 && string.Equals(parts[0], "packages", StringComparison.Ordinal) && SafePart(parts[1]))
                return await ResolveNuGetAsync(new("https://api.nuget.org/v3/index.json"),
                    parts[1], parts.Length == 3 ? parts[2] : null, null, null, cancellationToken).ConfigureAwait(false);
            if (parts.Length == 5 && string.Equals(parts[0], "api", StringComparison.Ordinal) && string.Equals(parts[1], "v2", StringComparison.Ordinal) && string.Equals(parts[2], "package", StringComparison.Ordinal))
                return await ResolveNuGetAsync(new("https://api.nuget.org/v3/index.json"),
                    parts[3], parts[4], null, null, cancellationToken).ConfigureAwait(false);
        }
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var choices = await ResolveGitHubAsync(uri, parts, githubToken, cancellationToken).ConfigureAwait(false);
            if (choices is not null) return choices;
        }

        if (IsArchive(uri.AbsolutePath))
        {
            var owner = string.Equals(uri.Host, "nuget.pkg.github.com", StringComparison.Ordinal) && parts.Length > 0 && SafePart(parts[0]) ? parts[0] : null;
            if (owner is not null && string.IsNullOrWhiteSpace(githubToken))
                throw new InvalidDataException("GitHub Packages requires an explicit read:packages token.");
            return [new(Path.GetFileName(uri.AbsolutePath), Download: uri,
                GitHubUsername: owner is null ? null : await ResolveGitHubUsernameAsync(githubToken!, cancellationToken).ConfigureAwait(false))];
        }
        throw new InvalidDataException("Unsupported package link. Use a NuGet gallery/version, GitHub release, package version, or direct module archive link.");
    }

    private async Task<IReadOnlyList<ModulePackageSource>?> ResolveGitHubAsync(
        Uri uri, string[] parts, string? githubToken, CancellationToken cancellationToken)
    {

        if (parts.Length >= 3 && string.Equals(parts[2], "releases", StringComparison.Ordinal) && SafePart(parts[0]) && SafePart(parts[1]))
        {
            var assets = await ResolveGitHubReleaseAsync(uri, parts, cancellationToken).ConfigureAwait(false);
            if (assets is not null) return assets;
        }

        // A repository package page selects an explicit version, or exposes feed versions as choices.
        if (parts.Length is 5 or 6 && string.Equals(parts[2], "pkgs", StringComparison.Ordinal) && string.Equals(parts[3], "nuget", StringComparison.Ordinal) &&
            SafePart(parts[0]) && SafePart(parts[1]) && SafePart(parts[4]))
        {
            if (string.IsNullOrWhiteSpace(githubToken)) throw new InvalidDataException("GitHub Packages requires an explicit read:packages token.");
            if (parts.Length == 5)
                return await ResolveNuGetAsync(new($"https://nuget.pkg.github.com/{parts[0]}/index.json"),
                    parts[4], null, await ResolveGitHubUsernameAsync(githubToken, cancellationToken).ConfigureAwait(false),
                    githubToken, cancellationToken).ConfigureAwait(false);
            return await ResolveGitHubPackageVersionAsync(parts, githubToken, cancellationToken).ConfigureAwait(false);
        }
        if (parts.Length == 5 && (string.Equals(parts[0], "orgs", StringComparison.Ordinal) || string.Equals(parts[0], "users", StringComparison.Ordinal)) && string.Equals(parts[2], "packages", StringComparison.Ordinal) &&
            string.Equals(parts[3], "nuget", StringComparison.Ordinal) && SafePart(parts[1]) && SafePart(parts[4]))
        {
            if (string.IsNullOrWhiteSpace(githubToken)) throw new InvalidDataException("GitHub Packages requires an explicit read:packages token.");
            return await ResolveNuGetAsync(new($"https://nuget.pkg.github.com/{parts[1]}/index.json"),
                parts[4], null, await ResolveGitHubUsernameAsync(githubToken, cancellationToken).ConfigureAwait(false),
                githubToken, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<IReadOnlyList<ModulePackageSource>?> ResolveGitHubReleaseAsync(
        Uri uri, string[] parts, CancellationToken cancellationToken)
    {

        if (parts.Length >= 6 && string.Equals(parts[3], "download", StringComparison.Ordinal) && IsArchive(parts[^1]))
            return [new(parts[^1], Download: uri)];
        var suffix = parts.Length == 3 || (string.Equals(parts[3], "latest", StringComparison.Ordinal) && parts.Length == 4) ? "latest" : string.Equals(parts[3], "tag", StringComparison.Ordinal) && parts.Length >= 5
                ? "tags/" + Uri.EscapeDataString(string.Join('/', parts.Skip(4))) : null;
        if (suffix is not null)
        {
            using var release = await ReadJsonAsync(new($"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases/{suffix}"),
                null, null, cancellationToken).ConfigureAwait(false);
            var assets = release.RootElement.GetProperty("assets").EnumerateArray()
                .Where(asset => IsArchive(asset.GetProperty("name").GetString()!))
                .Select(asset => new ModulePackageSource(asset.GetProperty("name").GetString()!,
                    Download: new Uri(asset.GetProperty("browser_download_url").GetString()!)))
                .Take(65).ToArray();
            if (assets.Length is 0 or > 64) throw new InvalidDataException("The release must contain 1–64 .nupkg or .zip module assets.");
            foreach (var asset in assets) RequireDownloadUri(asset.Download!);
            return assets;
        }
        return null;
    }

    private async Task<IReadOnlyList<ModulePackageSource>> ResolveGitHubPackageVersionAsync(
        string[] parts, string githubToken, CancellationToken cancellationToken)
    {
        if (!long.TryParse(parts[5], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var versionId) || versionId <= 0)
            throw new InvalidDataException("Invalid GitHub package version identity.");
        var api = new Uri($"https://api.github.com/orgs/{parts[0]}/packages/nuget/{parts[4]}/versions/{versionId}");
        JsonDocument package;
        try { package = await ReadJsonAsync(api, parts[0], githubToken, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            package = await ReadJsonAsync(new($"https://api.github.com/users/{parts[0]}/packages/nuget/{parts[4]}/versions/{versionId}"),
                parts[0], githubToken, cancellationToken).ConfigureAwait(false);
        }
        using (package)
            return await ResolveNuGetAsync(new($"https://nuget.pkg.github.com/{parts[0]}/index.json"),
                parts[4], package.RootElement.GetProperty("name").GetString(),
                await ResolveGitHubUsernameAsync(githubToken, cancellationToken).ConfigureAwait(false),
                githubToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveGitHubUsernameAsync(string token, CancellationToken cancellationToken)
    {
        using var identity = await ReadJsonAsync(new("https://api.github.com/user"), "explicit", token, cancellationToken).ConfigureAwait(false);
        var username = identity.RootElement.GetProperty("login").GetString();
        if (!SafePart(username)) throw new InvalidDataException("GitHub returned an invalid package login identity.");
        return username!;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308",
        Justification = "NuGet V3 flat-container coordinates require lowercase package IDs and normalized versions; ordinal identity checks are separate.")]
    private async Task<IReadOnlyList<ModulePackageSource>> ResolveNuGetAsync(
        Uri index, string id, string? version, string? owner, string? token, CancellationToken cancellationToken)
    {
        if (!SafePart(id) || (version is not null && !SafePart(version))) throw new InvalidDataException("Invalid package identity.");
        using var serviceIndex = await ReadJsonAsync(index, owner, token, cancellationToken).ConfigureAwait(false);
        var baseAddress = serviceIndex.RootElement.GetProperty("resources").EnumerateArray()
            .First(resource => string.Equals(resource.GetProperty("@type").GetString(), "PackageBaseAddress/3.0.0", StringComparison.Ordinal))
            .GetProperty("@id").GetString()!;
        var root = new Uri(baseAddress.TrimEnd('/') + "/");
        RequireDownloadUri(root);
        if (!root.Host.Equals(index.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package content must remain on its source's authority.");
        var lowerId = id.ToLowerInvariant();
        using var versions = await ReadJsonAsync(new(root, $"{lowerId}/index.json"), owner, token, cancellationToken).ConfigureAwait(false);
        var choices = versions.RootElement.GetProperty("versions").EnumerateArray()
            .Select(item => item.GetString()!).Where(SafePart).ToArray();
        if (version is not null)
        {
            choices = choices.Where(item => string.Equals(item, version, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (choices.Length != 1) throw new InvalidDataException("That exact package version is unavailable. Use the normalized gallery version.");
        }
        else
        {
            choices = choices.OrderByDescending(item => !item.Contains('-', StringComparison.Ordinal))
                .ThenByDescending(item => System.Version.TryParse(item.Split('-')[0], out var number) ? number : new System.Version())
                .ThenByDescending(item => item, StringComparer.Ordinal).Take(64).ToArray();
        }
        if (choices.Length == 0) throw new InvalidDataException("No package versions are available.");
        return choices.Select(item => new ModulePackageSource($"{id} {item}",
            Download: new(root, $"{lowerId}/{item}/{lowerId}.{item}.nupkg"),
            PackageId: id, Version: item, GitHubUsername: owner)).ToArray();
    }

    private async Task<JsonDocument> ReadJsonAsync(Uri uri, string? owner, string? token, CancellationToken cancellationToken)
    {
        using var response = await OpenAsync(uri, owner, token, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var bodyAsyncDisposal = body.ConfigureAwait(false);
        using var bounded = new MemoryStream();
        await CopyBoundedAsync(body, bounded, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(bounded.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }

    internal async Task DownloadAsync(ModulePackageSource source, Stream destination, string? token, CancellationToken cancellationToken)
    {
        using var response = await OpenAsync(source.Download!, source.GitHubUsername, token, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ModulePackageStore.MaximumArchiveBytes)
            throw new InvalidDataException("Module archive exceeds the download limit.");
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var bodyAsyncDisposal = body.ConfigureAwait(false);
        await CopyBoundedAsync(body, destination, ModulePackageStore.MaximumArchiveBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> OpenAsync(Uri initial, string? owner, string? token, CancellationToken cancellationToken)
    {
        var current = initial;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            RequireDownloadUri(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("SharpClaw/0.5.0");
            if (owner is not null && token is not null &&
                current.Host.Equals(initial.Host, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(current.Host, "api.github.com", StringComparison.Ordinal) || string.Equals(current.Host, "nuget.pkg.github.com", StringComparison.Ordinal)))
            {
                request.Headers.Authorization = string.Equals(current.Host, "api.github.com", StringComparison.Ordinal) ? new AuthenticationHeaderValue("Bearer", token)
                    : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{owner}:{token}")));
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Package redirect has no location.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                // Once redirected, never reattach a credential, even on a later same-host hop.
                owner = null;
                token = null;
                continue;
            }
            return response;
        }
        throw new InvalidDataException("Too many package redirects.");
    }

    internal static void RequireDownloadUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Host is not
                ("nuget.org" or "www.nuget.org" or "api.nuget.org" or "globalcdn.nuget.org" or
                 "github.com" or "api.github.com" or "nuget.pkg.github.com" or
                 "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            throw new InvalidDataException("Only HTTPS NuGet/GitHub package and release authorities are accepted, without embedded credentials.");
    }

    internal static async Task CopyBoundedAsync(Stream source, Stream destination, long maximum, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total = checked(total + read);
            if (total > maximum) throw new InvalidDataException("Module input exceeds its size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool SafePart(string? value) => SharpClawModuleSettings.IsIdentifier(value) && !string.Equals(value, ".", StringComparison.Ordinal) && !string.Equals(value, "..", StringComparison.Ordinal);
    private static bool IsArchive(string value) => value.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    public void Dispose() => _http.Dispose();
}
