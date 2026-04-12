using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.DTOs.SearchEngines;
using SharpClaw.Contracts.Enums;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Modules.WebAccess.Services;

/// <summary>
/// Manages search engine CRUD and dispatches search queries to
/// the appropriate provider API based on <see cref="SearchEngineType"/>.
/// </summary>
public sealed class SearchEngineService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions)
{
    /// <summary>Maximum response body size (1 MB) to prevent memory exhaustion.</summary>
    private const int MaxResponseBytes = 1 * 1024 * 1024;

    // ═══════════════════════════════════════════════════════════════
    // CRUD
    // ═══════════════════════════════════════════════════════════════

    public async Task<SearchEngineResponse> CreateAsync(
        CreateSearchEngineRequest request, CancellationToken ct = default)
    {
        var engine = new SearchEngineDB
        {
            Name = request.Name,
            Type = request.Type,
            Endpoint = request.Endpoint,
            Description = request.Description,
        };

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            engine.EncryptedApiKey = ApiKeyEncryptor.Encrypt(request.ApiKey, encryptionOptions.Key);

        if (!string.IsNullOrWhiteSpace(request.SecondaryKey))
            engine.EncryptedSecondaryKey = ApiKeyEncryptor.Encrypt(request.SecondaryKey, encryptionOptions.Key);

        db.SearchEngines.Add(engine);
        await db.SaveChangesAsync(ct);
        return ToResponse(engine);
    }

    public async Task<IReadOnlyList<SearchEngineResponse>> ListAsync(
        CancellationToken ct = default)
    {
        var engines = await db.SearchEngines
            .OrderBy(e => e.Name)
            .ToListAsync(ct);
        return engines.Select(ToResponse).ToList();
    }

    public async Task<SearchEngineResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var engine = await db.SearchEngines.FirstOrDefaultAsync(e => e.Id == id, ct);
        return engine is null ? null : ToResponse(engine);
    }

    public async Task<SearchEngineResponse?> UpdateAsync(
        Guid id, UpdateSearchEngineRequest request, CancellationToken ct = default)
    {
        var engine = await db.SearchEngines.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (engine is null) return null;

        if (request.Name is not null) engine.Name = request.Name;
        if (request.Type.HasValue) engine.Type = request.Type.Value;
        if (request.Endpoint is not null) engine.Endpoint = request.Endpoint;
        if (request.Description is not null) engine.Description = request.Description;

        if (request.ApiKey is not null)
            engine.EncryptedApiKey = string.IsNullOrWhiteSpace(request.ApiKey)
                ? null
                : ApiKeyEncryptor.Encrypt(request.ApiKey, encryptionOptions.Key);

        if (request.SecondaryKey is not null)
            engine.EncryptedSecondaryKey = string.IsNullOrWhiteSpace(request.SecondaryKey)
                ? null
                : ApiKeyEncryptor.Encrypt(request.SecondaryKey, encryptionOptions.Key);

        await db.SaveChangesAsync(ct);
        return ToResponse(engine);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var engine = await db.SearchEngines.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (engine is null) return false;
        db.SearchEngines.Remove(engine);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // Module entry point — called from WebAccessModule.ExecuteToolAsync
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Bridge called by <see cref="WebAccessModule.ExecuteToolAsync"/>:
    /// deserializes tool parameters, resolves the <see cref="SearchEngineDB"/>
    /// resource, and delegates to <see cref="QueryAsync"/>.
    /// </summary>
    public async Task<string> ExecuteQueryAsync(
        JsonElement parameters, AgentJobContext job, CancellationToken ct)
    {
        var resourceId = job.ResourceId
            ?? (parameters.TryGetProperty("targetId", out var tid) && Guid.TryParse(tid.GetString(), out var parsed)
                ? parsed
                : (parameters.TryGetProperty("resourceId", out var rid) && Guid.TryParse(rid.GetString(), out var rparsed)
                    ? rparsed
                    : throw new InvalidOperationException(
                        "query_search_engine requires a targetId (search engine resource GUID).")));

        var engine = await db.SearchEngines
            .Include(e => e.Skill)
            .FirstOrDefaultAsync(e => e.Id == resourceId, ct)
            ?? throw new InvalidOperationException(
                $"Search engine {resourceId} not found.");

        var query = parameters.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException(
                "query_search_engine requires a 'query' field.");

        var result = await QueryAsync(
            engine, query,
            count: parameters.TryGetProperty("count", out var c) && c.TryGetInt32(out var cv) ? cv : 10,
            offset: parameters.TryGetProperty("offset", out var o) && o.TryGetInt32(out var ov) ? ov : 0,
            language: parameters.TryGetProperty("language", out var l) ? l.GetString() : null,
            region: parameters.TryGetProperty("region", out var r) ? r.GetString() : null,
            safeSearch: parameters.TryGetProperty("safeSearch", out var ss) ? ss.GetString() : null,
            dateRestrict: parameters.TryGetProperty("dateRestrict", out var dr) ? dr.GetString() : null,
            siteRestrict: parameters.TryGetProperty("siteRestrict", out var sr) ? sr.GetString() : null,
            fileType: parameters.TryGetProperty("fileType", out var ft) ? ft.GetString() : null,
            exactTerms: parameters.TryGetProperty("exactTerms", out var et) ? et.GetString() : null,
            excludeTerms: parameters.TryGetProperty("excludeTerms", out var ex) ? ex.GetString() : null,
            searchType: parameters.TryGetProperty("searchType", out var st) ? st.GetString() : null,
            sortBy: parameters.TryGetProperty("sortBy", out var sb) ? sb.GetString() : null,
            topic: parameters.TryGetProperty("topic", out var tp) ? tp.GetString() : null,
            category: parameters.TryGetProperty("category", out var cat) ? cat.GetString() : null,
            ct: ct);

        if (engine.Skill is { SkillText.Length: > 0 } skill)
            result = $"[Search Engine Skill: {skill.Name}]\n{skill.SkillText}\n\n---\n\n{result}";

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // Query execution — per-type API dispatch
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes a search query against the registered engine, building
    /// the HTTP request according to the engine's <see cref="SearchEngineType"/>.
    /// Returns the parsed/formatted search results as text.
    /// </summary>
    public async Task<string> QueryAsync(
        SearchEngineDB engine,
        string query,
        int count = 10,
        int offset = 0,
        string? language = null,
        string? region = null,
        string? safeSearch = null,
        string? dateRestrict = null,
        string? siteRestrict = null,
        string? fileType = null,
        string? exactTerms = null,
        string? excludeTerms = null,
        string? searchType = null,
        string? sortBy = null,
        string? topic = null,
        string? category = null,
        CancellationToken ct = default)
    {
        var apiKey = engine.EncryptedApiKey is not null
            ? ApiKeyEncryptor.Decrypt(engine.EncryptedApiKey, encryptionOptions.Key)
            : null;

        var secondaryKey = engine.EncryptedSecondaryKey is not null
            ? ApiKeyEncryptor.Decrypt(engine.EncryptedSecondaryKey, encryptionOptions.Key)
            : null;

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; SharpClaw/1.0; +https://github.com/mkn8rn/SharpClaw)");

        return engine.Type switch
        {
            SearchEngineType.Google => await QueryGoogleAsync(
                httpClient, engine.Endpoint, apiKey, secondaryKey,
                query, count, offset, language, region, safeSearch,
                dateRestrict, siteRestrict, fileType, exactTerms,
                excludeTerms, searchType, sortBy, ct),

            SearchEngineType.Bing => await QueryBingAsync(
                httpClient, engine.Endpoint, apiKey,
                query, count, offset, language, region, safeSearch,
                siteRestrict, ct),

            SearchEngineType.DuckDuckGo => await QueryDuckDuckGoAsync(
                httpClient, engine.Endpoint, query, ct),

            SearchEngineType.Brave => await QueryBraveAsync(
                httpClient, engine.Endpoint, apiKey,
                query, count, offset, language, region, safeSearch, ct),

            SearchEngineType.SearXNG => await QuerySearXNGAsync(
                httpClient, engine.Endpoint,
                query, count, offset, language, safeSearch, category, ct),

            SearchEngineType.Tavily => await QueryTavilyAsync(
                httpClient, engine.Endpoint, apiKey,
                query, count, searchType, topic, ct),

            SearchEngineType.Serper => await QuerySerperAsync(
                httpClient, engine.Endpoint, apiKey,
                query, count, offset, language, region, ct),

            SearchEngineType.Kagi => await QueryKagiAsync(
                httpClient, engine.Endpoint, apiKey,
                query, count, offset, ct),

            SearchEngineType.YouDotCom => await QueryYouDotComAsync(
                httpClient, engine.Endpoint, apiKey,
                query, count, offset, region, safeSearch, ct),

            SearchEngineType.Mojeek => await QueryMojeekAsync(
                httpClient, engine.Endpoint, apiKey,
                query, count, offset, language, region, ct),

            SearchEngineType.Yandex => await QueryYandexAsync(
                httpClient, engine.Endpoint, apiKey, secondaryKey,
                query, count, offset, language, region, ct),

            SearchEngineType.Baidu => await QueryBaiduAsync(
                httpClient, engine.Endpoint, apiKey, secondaryKey,
                query, count, offset, ct),

            SearchEngineType.Custom or _ => await QueryCustomAsync(
                httpClient, engine.Endpoint, apiKey,
                query, count, offset, ct),
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Provider-specific implementations
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Google Custom Search JSON API.
    /// Endpoint: https://www.googleapis.com/customsearch/v1
    /// Auth: API key via <c>key</c> query param.
    /// Secondary key: Custom Search Engine ID (<c>cx</c>).
    /// </summary>
    private async Task<string> QueryGoogleAsync(
        HttpClient http, string endpoint, string? apiKey, string? cx,
        string query, int count, int offset,
        string? language, string? region, string? safeSearch,
        string? dateRestrict, string? siteRestrict, string? fileType,
        string? exactTerms, string? excludeTerms, string? searchType,
        string? sortBy, CancellationToken ct)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["q"] = query;
        qs["num"] = Math.Clamp(count, 1, 10).ToString();
        qs["start"] = (offset + 1).ToString(); // 1-based
        if (apiKey is not null) qs["key"] = apiKey;
        if (cx is not null) qs["cx"] = cx;
        if (language is not null) qs["lr"] = language.StartsWith("lang_") ? language : $"lang_{language}";
        if (region is not null) qs["gl"] = region;
        if (safeSearch is not null) qs["safe"] = safeSearch; // off, medium, high
        if (dateRestrict is not null) qs["dateRestrict"] = dateRestrict; // d[N], w[N], m[N], y[N]
        if (siteRestrict is not null) qs["siteSearch"] = siteRestrict;
        if (fileType is not null) qs["fileType"] = fileType;
        if (exactTerms is not null) qs["exactTerms"] = exactTerms;
        if (excludeTerms is not null) qs["excludeTerms"] = excludeTerms;
        if (searchType is not null) qs["searchType"] = searchType; // image
        if (sortBy is not null) qs["sort"] = sortBy; // date

        return await GetJsonResponseAsync(http, $"{endpoint}?{qs}", ct);
    }

    /// <summary>
    /// Bing Web Search API v7.
    /// Endpoint: https://api.bing.microsoft.com/v7.0/search
    /// Auth: <c>Ocp-Apim-Subscription-Key</c> header.
    /// </summary>
    private async Task<string> QueryBingAsync(
        HttpClient http, string endpoint, string? apiKey,
        string query, int count, int offset,
        string? language, string? region, string? safeSearch,
        string? siteRestrict, CancellationToken ct)
    {
        if (apiKey is not null)
            http.DefaultRequestHeaders.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", apiKey);

        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["q"] = siteRestrict is not null ? $"site:{siteRestrict} {query}" : query;
        qs["count"] = Math.Clamp(count, 1, 50).ToString();
        qs["offset"] = offset.ToString();
        if (language is not null) qs["setLang"] = language;
        if (region is not null) qs["mkt"] = region; // e.g. en-US
        if (safeSearch is not null) qs["safeSearch"] = safeSearch; // Off, Moderate, Strict

        return await GetJsonResponseAsync(http, $"{endpoint}?{qs}", ct);
    }

    /// <summary>
    /// DuckDuckGo Instant Answer API (no key required).
    /// Endpoint: https://api.duckduckgo.com/
    /// </summary>
    private async Task<string> QueryDuckDuckGoAsync(
        HttpClient http, string endpoint, string query, CancellationToken ct)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["q"] = query;
        qs["format"] = "json";
        qs["no_redirect"] = "1";
        qs["no_html"] = "1";

        return await GetJsonResponseAsync(http, $"{endpoint}?{qs}", ct);
    }

    /// <summary>
    /// Brave Search API.
    /// Endpoint: https://api.search.brave.com/res/v1/web/search
    /// Auth: <c>X-Subscription-Token</c> header.
    /// </summary>
    private async Task<string> QueryBraveAsync(
        HttpClient http, string endpoint, string? apiKey,
        string query, int count, int offset,
        string? language, string? region, string? safeSearch,
        CancellationToken ct)
    {
        if (apiKey is not null)
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Subscription-Token", apiKey);

        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["q"] = query;
        qs["count"] = Math.Clamp(count, 1, 20).ToString();
        qs["offset"] = offset.ToString();
        if (language is not null) qs["search_lang"] = language;
        if (region is not null) qs["country"] = region;
        if (safeSearch is not null) qs["safesearch"] = safeSearch; // off, moderate, strict

        return await GetJsonResponseAsync(http, $"{endpoint}?{qs}", ct);
    }

    /// <summary>
    /// SearXNG JSON API (self-hosted federated meta-search).
    /// Endpoint: https://your-instance/search
    /// Auth: none (or instance-level).
    /// </summary>
    private async Task<string> QuerySearXNGAsync(
        HttpClient http, string endpoint,
        string query, int count, int offset,
        string? language, string? safeSearch, string? category,
        CancellationToken ct)
    {
        var page = offset / Math.Max(count, 1) + 1;
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["q"] = query;
        qs["format"] = "json";
        qs["pageno"] = page.ToString();
        if (language is not null) qs["language"] = language;
        if (safeSearch is not null) qs["safesearch"] = safeSearch; // 0, 1, 2
        if (category is not null) qs["categories"] = category; // general, images, news, etc.

        return await GetJsonResponseAsync(http, $"{endpoint}?{qs}", ct);
    }

    /// <summary>
    /// Tavily AI-optimised search API.
    /// Endpoint: https://api.tavily.com/search
    /// Auth: API key in JSON body.
    /// </summary>
    private async Task<string> QueryTavilyAsync(
        HttpClient http, string endpoint, string? apiKey,
        string query, int count, string? searchType, string? topic,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["max_results"] = Math.Clamp(count, 1, 20),
            ["search_depth"] = searchType ?? "basic", // basic, advanced
        };
        if (apiKey is not null) body["api_key"] = apiKey;
        if (topic is not null) body["topic"] = topic; // general, news

        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.PostAsync(endpoint, content, ct);
        return await ReadSafeResponseAsync(response, ct);
    }

    /// <summary>
    /// Serper.dev Google SERP API.
    /// Endpoint: https://google.serper.dev/search
    /// Auth: <c>X-API-KEY</c> header.
    /// </summary>
    private async Task<string> QuerySerperAsync(
        HttpClient http, string endpoint, string? apiKey,
        string query, int count, int offset,
        string? language, string? region, CancellationToken ct)
    {
        if (apiKey is not null)
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-KEY", apiKey);

        var body = new Dictionary<string, object?>
        {
            ["q"] = query,
            ["num"] = Math.Clamp(count, 1, 100),
        };
        if (offset > 0) body["page"] = offset / Math.Max(count, 1) + 1;
        if (language is not null) body["hl"] = language;
        if (region is not null) body["gl"] = region;

        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.PostAsync(endpoint, content, ct);
        return await ReadSafeResponseAsync(response, ct);
    }

    /// <summary>
    /// Kagi Search API.
    /// Endpoint: https://kagi.com/api/v0/search
    /// Auth: <c>Authorization: Bot {apiKey}</c>.
    /// </summary>
    private async Task<string> QueryKagiAsync(
        HttpClient http, string endpoint, string? apiKey,
        string query, int count, int offset, CancellationToken ct)
    {
        if (apiKey is not null)
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bot {apiKey}");

        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["q"] = query;
        qs["limit"] = Math.Clamp(count, 1, 50).ToString();
        if (offset > 0) qs["offset"] = offset.ToString();

        return await GetJsonResponseAsync(http, $"{endpoint}?{qs}", ct);
    }

    /// <summary>
    /// You.com Search API.
    /// Endpoint: https://api.ydc-index.io/search
    /// Auth: <c>X-API-Key</c> header.
    /// </summary>
    private async Task<string> QueryYouDotComAsync(
        HttpClient http, string endpoint, string? apiKey,
        string query, int count, int offset,
        string? region, string? safeSearch, CancellationToken ct)
    {
        if (apiKey is not null)
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", apiKey);

        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["query"] = query;
        qs["num_web_results"] = Math.Clamp(count, 1, 20).ToString();
        if (offset > 0) qs["offset"] = offset.ToString();
        if (region is not null) qs["country"] = region;
        if (safeSearch is not null) qs["safesearch"] = safeSearch;

        return await GetJsonResponseAsync(http, $"{endpoint}?{qs}", ct);
    }

    /// <summary>
    /// Mojeek Search API.
    /// Endpoint: https://www.mojeek.com/search
    /// Auth: <c>api_key</c> query param.
    /// </summary>
    private async Task<string> QueryMojeekAsync(
        HttpClient http, string endpoint, string? apiKey,
        string query, int count, int offset,
        string? language, string? region, CancellationToken ct)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["q"] = query;
        qs["fmt"] = "json";
        qs["t"] = Math.Clamp(count, 1, 50).ToString();
        qs["s"] = (offset + 1).ToString(); // 1-based
        if (apiKey is not null) qs["api_key"] = apiKey;
        if (language is not null) qs["lb"] = language;
        if (region is not null) qs["rc"] = region;

        return await GetJsonResponseAsync(http, $"{endpoint}?{qs}", ct);
    }

    /// <summary>
    /// Yandex Search API (XML).
    /// Endpoint: https://yandex.com/search/xml
    /// Auth: <c>apikey</c> query param.
    /// Secondary key: folder ID (<c>folderid</c>).
    /// </summary>
    private async Task<string> QueryYandexAsync(
        HttpClient http, string endpoint, string? apiKey, string? folderId,
        string query, int count, int offset,
        string? language, string? region, CancellationToken ct)
    {
        var page = offset / Math.Max(count, 1);
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["query"] = query;
        qs["groupby"] = $"attr=d.mode=deep.groups-on-page={Math.Clamp(count, 1, 100)}.docs-in-group=1";
        qs["page"] = page.ToString();
        if (apiKey is not null) qs["apikey"] = apiKey;
        if (folderId is not null) qs["folderid"] = folderId;
        if (language is not null) qs["l10n"] = language;
        if (region is not null) qs["lr"] = region;

        // Yandex returns XML by default — accept both
        http.DefaultRequestHeaders.Accept.ParseAdd("application/xml, application/json;q=0.9");
        return await GetTextResponseAsync(http, $"{endpoint}?{qs}", ct);
    }

    /// <summary>
    /// Baidu Search API.
    /// Endpoint: custom (Baidu doesn't have an official REST API;
    /// typically a proxy/wrapper endpoint).
    /// Auth: API key + secret key.
    /// </summary>
    private async Task<string> QueryBaiduAsync(
        HttpClient http, string endpoint, string? apiKey, string? secretKey,
        string query, int count, int offset, CancellationToken ct)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["q"] = query;
        qs["rn"] = Math.Clamp(count, 1, 50).ToString();
        qs["pn"] = offset.ToString();
        if (apiKey is not null) qs["apikey"] = apiKey;
        if (secretKey is not null) qs["secretkey"] = secretKey;

        return await GetJsonResponseAsync(http, $"{endpoint}?{qs}", ct);
    }

    /// <summary>
    /// Generic custom search engine — passes query, count, and offset
    /// as simple query-string parameters. The endpoint is responsible
    /// for all other configuration.
    /// </summary>
    private async Task<string> QueryCustomAsync(
        HttpClient http, string endpoint, string? apiKey,
        string query, int count, int offset, CancellationToken ct)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["q"] = query;
        qs["count"] = count.ToString();
        qs["offset"] = offset.ToString();
        if (apiKey is not null) qs["api_key"] = apiKey;

        return await GetJsonResponseAsync(http, $"{endpoint}?{qs}", ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> GetJsonResponseAsync(
        HttpClient http, string url, CancellationToken ct)
    {
        http.DefaultRequestHeaders.Accept.TryParseAdd("application/json");
        var response = await http.GetAsync(url, ct);
        return await ReadSafeResponseAsync(response, ct);
    }

    private async Task<string> GetTextResponseAsync(
        HttpClient http, string url, CancellationToken ct)
    {
        var response = await http.GetAsync(url, ct);
        return await ReadSafeResponseAsync(response, ct);
    }

    private static async Task<string> ReadSafeResponseAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadCappedBodyAsync(response, ct);
            throw new InvalidOperationException(
                $"Search API returned HTTP {(int)response.StatusCode}: {errorBody}");
        }

        return await ReadCappedBodyAsync(response, ct);
    }

    private static async Task<string> ReadCappedBodyAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxResponseBytes / sizeof(char)];
        var charsRead = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        var body = new string(buffer, 0, charsRead);

        if (charsRead == buffer.Length)
            body += "\n\n[TRUNCATED — response exceeded 1 MB limit]";

        return body;
    }

    private static SearchEngineResponse ToResponse(SearchEngineDB engine) =>
        new(engine.Id, engine.Name, engine.Type, engine.Endpoint,
            engine.Description,
            HasApiKey: engine.EncryptedApiKey is not null,
            HasSecondaryKey: engine.EncryptedSecondaryKey is not null,
            engine.CreatedAt, engine.UpdatedAt);
}
