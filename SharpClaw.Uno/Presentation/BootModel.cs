using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed record BootState(
    string Icon,
    Windows.UI.Color IconColor,
    string Text,
    Windows.UI.Color TextColor,
    bool IsSpinnerActive,
    bool IsRetryVisible);

public sealed class BootModel
{
    private readonly BackendProcessManager _backend;
    private readonly SharpClawApiClient _api;

    public BootModel(
        BackendProcessManager backend,
        SharpClawApiClient api)
    {
        _backend = backend;
        _api = api;
    }

    public event EventHandler<BootState>? StateChanged;
    public event EventHandler? NavigateRequested;

    public BootState CurrentState { get; private set; } = Connecting;

    public bool IsAwaitingInput { get; private set; }

    /// <summary>Current API base URL (for display / editing).</summary>
    public string ApiUrl => _backend.ApiUrl;

    private static readonly Windows.UI.Color Gray = Windows.UI.Color.FromArgb(255, 128, 128, 128);
    private static readonly Windows.UI.Color LightGray = Windows.UI.Color.FromArgb(255, 204, 204, 204);
    private static readonly Windows.UI.Color Green = Windows.UI.Color.FromArgb(255, 50, 205, 50);
    private static readonly Windows.UI.Color Red = Windows.UI.Color.FromArgb(255, 255, 68, 68);

    private static readonly BootState Connecting =
        new(">", Gray, "Connecting to SharpClaw...", LightGray, true, false);

    public async Task ConnectAsync(string? customUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(customUrl))
        {
            var url = customUrl.Trim();
            _backend.UpdateApiUrl(url);
            _api.UpdateBaseUrl(url);
        }

        IsAwaitingInput = false;
        SetState(Connecting);

        try
        {
            await _backend.EnsureStartedAsync();

            // Always re-read the API key from disk and validate it
            // against the running process.  In external/dev mode the
            // API may have been restarted (new key), and in bundled
            // mode we need to wait for the key file to appear.
            _api.InvalidateApiKey();
            await _api.WaitForReadyAsync(
                TimeSpan.FromSeconds(_backend.IsExternal ? 5 : 15));

            SetState(new("✓", Green, "Connection established.", Green, false, false));
            NavigateRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            IsAwaitingInput = true;
            SetState(new("✗", Red, "Unable to reach the SharpClaw service.", Red, false, true));
        }
    }

    private void SetState(BootState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(this, state);
    }
}
