using System;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility.UI;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension.ToolWindows;

[DataContract]
internal sealed class SharpClawBackendOptionItem
{
    public SharpClawBackendOptionItem(string? key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    [DataMember] public string? Key { get; }
    [DataMember] public string DisplayName { get; }
}

/// <summary>
/// Options surface for selecting a discovered SharpClaw backend or applying
/// explicit endpoint/authentication overrides before connecting.
/// </summary>
[DataContract]
internal sealed class SharpClawOptionsViewModel : NotifyPropertyChangedObject
{
    private readonly SharpClawConnectionOptionsStore _optionsStore;
    private readonly SharpClawConnector _connector;
    private readonly SharpClawOutputLog _log;
    private readonly SynchronizationContext? _uiContext;

    private int _selectedInstanceIndex;
    private string _baseUrlOverride = string.Empty;
    private string _portOverride = string.Empty;
    private string _apiKeyOverride = string.Empty;
    private string _apiKeyFileOverride = string.Empty;
    private string _gatewayTokenOverride = string.Empty;
    private string _gatewayTokenFileOverride = string.Empty;
    private bool _preferAliveInstances = true;
    private string _status = "Options loaded.";

    public SharpClawOptionsViewModel(
        SharpClawConnectionOptionsStore optionsStore,
        SharpClawConnector connector,
        SharpClawOutputLog log,
        SynchronizationContext? uiContext)
    {
        _optionsStore = optionsStore;
        _connector = connector;
        _log = log;
        _uiContext = uiContext;

        RefreshCommand = new AsyncCommand(async (_, ct) => await RefreshAsync(ct).ConfigureAwait(false));
        SaveCommand = new AsyncCommand(async (_, ct) => await SaveAsync(ct).ConfigureAwait(false));
        ResetCommand = new AsyncCommand(async (_, ct) => await ResetAsync(ct).ConfigureAwait(false));
        ConnectCommand = new AsyncCommand(async (_, ct) => await ConnectAsync(ct).ConfigureAwait(false));

        LoadFromDisk(refreshDetectedInstances: true);
    }

    [DataMember] public ObservableList<SharpClawBackendOptionItem> DetectedInstances { get; } = new();
    [DataMember] public IAsyncCommand RefreshCommand { get; }
    [DataMember] public IAsyncCommand SaveCommand { get; }
    [DataMember] public IAsyncCommand ResetCommand { get; }
    [DataMember] public IAsyncCommand ConnectCommand { get; }

    [DataMember]
    public int SelectedInstanceIndex
    {
        get => _selectedInstanceIndex;
        set
        {
            if (value < 0 || value >= DetectedInstances.Count)
                return;

            SetProperty(ref _selectedInstanceIndex, value);
        }
    }

    [DataMember]
    public string BaseUrlOverride
    {
        get => _baseUrlOverride;
        set => SetProperty(ref _baseUrlOverride, value ?? string.Empty);
    }

    [DataMember]
    public string PortOverride
    {
        get => _portOverride;
        set => SetProperty(ref _portOverride, value ?? string.Empty);
    }

    [DataMember]
    public string ApiKeyOverride
    {
        get => _apiKeyOverride;
        set => SetProperty(ref _apiKeyOverride, value ?? string.Empty);
    }

    [DataMember]
    public string ApiKeyFileOverride
    {
        get => _apiKeyFileOverride;
        set => SetProperty(ref _apiKeyFileOverride, value ?? string.Empty);
    }

    [DataMember]
    public string GatewayTokenOverride
    {
        get => _gatewayTokenOverride;
        set => SetProperty(ref _gatewayTokenOverride, value ?? string.Empty);
    }

    [DataMember]
    public string GatewayTokenFileOverride
    {
        get => _gatewayTokenFileOverride;
        set => SetProperty(ref _gatewayTokenFileOverride, value ?? string.Empty);
    }

    [DataMember]
    public bool PreferAliveInstances
    {
        get => _preferAliveInstances;
        set
        {
            if (SetProperty(ref _preferAliveInstances, value))
                RefreshDetectedInstances(preserveSelectionKey: SelectedInstanceKey());
        }
    }

    [DataMember]
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value ?? string.Empty);
    }

    private SyncContextAwaitable SwitchToUi() => new(_uiContext);

    private async Task RefreshAsync(CancellationToken ct)
    {
        await SwitchToUi();
        if (ct.IsCancellationRequested)
            return;

        RefreshDetectedInstances(preserveSelectionKey: SelectedInstanceKey());
        Status = $"Detected {Math.Max(0, DetectedInstances.Count - 1)} backend instance(s).";
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await SwitchToUi();
        if (ct.IsCancellationRequested)
            return;

        if (!TryBuildOptions(out var options))
            return;

        _optionsStore.Save(options);
        await _log.WriteLineAsync($"Options saved: {_optionsStore.DescribeOverrides(options)}").ConfigureAwait(false);
        Status = "Options saved.";
    }

    private async Task ResetAsync(CancellationToken ct)
    {
        await SwitchToUi();
        if (ct.IsCancellationRequested)
            return;

        _optionsStore.Reset();
        LoadFromDisk(refreshDetectedInstances: true);
        await _log.WriteLineAsync("Options reset to discovery defaults.").ConfigureAwait(false);
        Status = "Options reset to discovery defaults.";
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await SwitchToUi();
        if (ct.IsCancellationRequested)
            return;

        if (!TryBuildOptions(out var options))
            return;

        _optionsStore.Save(options);
        Status = "Connecting...";
        var result = await _connector.ConnectAsync("options page", ct).ConfigureAwait(false);

        await SwitchToUi();
        Status = result.Success ? "Connected." : $"Connect failed: {result.Summary}";
    }

    private void LoadFromDisk(bool refreshDetectedInstances)
    {
        var options = _optionsStore.Load();
        BaseUrlOverride = options.BaseUrlOverride ?? string.Empty;
        PortOverride = options.PortOverride?.ToString() ?? string.Empty;
        ApiKeyOverride = options.ApiKeyOverride ?? string.Empty;
        ApiKeyFileOverride = options.ApiKeyFileOverride ?? string.Empty;
        GatewayTokenOverride = options.GatewayTokenOverride ?? string.Empty;
        GatewayTokenFileOverride = options.GatewayTokenFileOverride ?? string.Empty;
        PreferAliveInstances = options.PreferAliveInstances;

        if (refreshDetectedInstances)
            RefreshDetectedInstances(options.SelectedInstanceId);
    }

    private void RefreshDetectedInstances(string? preserveSelectionKey)
    {
        DetectedInstances.Clear();
        DetectedInstances.Add(new SharpClawBackendOptionItem(null, "Auto-select best detected backend"));

        var entries = _optionsStore.EnumerateDetectedInstances(PreferAliveInstances);
        foreach (var entry in entries)
        {
            var key = SharpClawConnectionOptionsStore.EntryKey(entry);
            DetectedInstances.Add(new SharpClawBackendOptionItem(key, FormatEntry(entry)));
        }

        var selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(preserveSelectionKey))
        {
            for (var i = 1; i < DetectedInstances.Count; i++)
            {
                if (string.Equals(DetectedInstances[i].Key, preserveSelectionKey, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }
        }

        SelectedInstanceIndex = selectedIndex;
    }

    private bool TryBuildOptions(out SharpClawConnectionOptions options)
    {
        options = new SharpClawConnectionOptions
        {
            SelectedInstanceId = SelectedInstanceKey(),
            BaseUrlOverride = ToNull(BaseUrlOverride),
            ApiKeyOverride = ToNull(ApiKeyOverride),
            ApiKeyFileOverride = ToNull(ApiKeyFileOverride),
            GatewayTokenOverride = ToNull(GatewayTokenOverride),
            GatewayTokenFileOverride = ToNull(GatewayTokenFileOverride),
            PreferAliveInstances = PreferAliveInstances,
        };

        var portText = ToNull(PortOverride);
        if (portText is null)
            return true;

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            Status = "Port override must be a number from 1 to 65535.";
            return false;
        }

        options.PortOverride = port;
        return true;
    }

    private string? SelectedInstanceKey()
    {
        if (SelectedInstanceIndex <= 0 || SelectedInstanceIndex >= DetectedInstances.Count)
            return null;

        return DetectedInstances[SelectedInstanceIndex].Key;
    }

    private static string FormatEntry(SharpClawDiscoveryEntry entry)
    {
        var alive = entry.IsAlive ? "alive" : "not running";
        var api = entry.HasApiKeyOnDisk ? "API key present" : "API key missing";
        var gateway = entry.HasGatewayTokenOnDisk ? "gateway present" : "gateway absent";
        return $"{entry.BaseUrl ?? "<no base url>"}  |  instance {SharpClawConnectionOptionsStore.Short(entry.InstanceId)}  |  pid {entry.ProcessId?.ToString() ?? "?"}  |  {alive}, {api}, {gateway}";
    }

    private static string? ToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
