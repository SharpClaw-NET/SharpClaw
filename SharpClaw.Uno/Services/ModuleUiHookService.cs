using System.Text.Json;
using SharpClaw.Helpers;

namespace SharpClaw.Services;

/// <summary>
/// Client-side service that fetches module UI contributions from the API
/// and provides them to pages for rendering dynamic module-owned elements.
/// </summary>
internal sealed class ModuleUiHookService
{
    private Dictionary<string, List<UiContributionEntry>> _contributions = new(StringComparer.Ordinal);

    /// <summary>Get all contributions for a specific contribution point.</summary>
    public IReadOnlyList<UiContributionEntry> GetContributions(string contributionPoint)
        => _contributions.TryGetValue(contributionPoint, out var list) ? list : [];

    /// <summary>Refresh contributions from the API.</summary>
    public async Task RefreshAsync(SharpClawApiClient api)
    {
        try
        {
            using var resp = await api.GetAsync("/modules/ui-contributions");
            if (!resp.IsSuccessStatusCode) return;

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var dict = new Dictionary<string, List<UiContributionEntry>>(StringComparer.Ordinal);

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var point = prop.Name;
                var list = new List<UiContributionEntry>();

                foreach (var item in prop.Value.EnumerateArray())
                {
                    var entry = new UiContributionEntry
                    {
                        ModuleId = item.GetProperty("moduleId").GetString() ?? "",
                        ContributionPoint = item.GetProperty("contributionPoint").GetString() ?? "",
                        ElementType = item.GetProperty("elementType").GetString() ?? "",
                        ElementId = item.GetProperty("elementId").GetString() ?? "",
                        Icon = item.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null,
                        Label = item.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? lb.GetString() : null,
                        Tooltip = item.TryGetProperty("tooltip", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null,
                        ActionToolName = item.TryGetProperty("actionToolName", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null,
                        RequiredModuleId = item.TryGetProperty("requiredModuleId", out var rm) && rm.ValueKind == JsonValueKind.String ? rm.GetString() : null,
                    };

                    if (item.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
                    {
                        var meta = new Dictionary<string, string>();
                        foreach (var mp in md.EnumerateObject())
                            if (mp.Value.ValueKind == JsonValueKind.String)
                                meta[mp.Name] = mp.Value.GetString()!;
                        entry.Metadata = meta;
                    }

                    list.Add(entry);
                }

                dict[point] = list;
            }

            _contributions = dict;
        }
        catch
        {
            // API unreachable — keep previous cache
        }
    }

    internal sealed class UiContributionEntry
    {
        public required string ModuleId { get; init; }
        public required string ContributionPoint { get; init; }
        public required string ElementType { get; init; }
        public required string ElementId { get; init; }
        public string? Icon { get; init; }
        public string? Label { get; init; }
        public string? Tooltip { get; init; }
        public string? ActionToolName { get; init; }
        public string? RequiredModuleId { get; init; }
        public IReadOnlyDictionary<string, string>? Metadata { get; set; }
    }
}
