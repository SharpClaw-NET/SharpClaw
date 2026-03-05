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
    private readonly INavigator _navigator;
    private readonly IServiceProvider _services;

    public BootModel(
        BackendProcessManager backend,
        SharpClawApiClient api,
        INavigator navigator,
        IServiceProvider services)
    {
        _backend = backend;
        _api = api;
        _navigator = navigator;
        _services = services;
    }

    public event EventHandler<BootState>? StateChanged;

    public BootState CurrentState { get; private set; } = Connecting;

    public bool IsAwaitingInput { get; private set; }

    private static readonly Windows.UI.Color Gray = Windows.UI.Color.FromArgb(255, 128, 128, 128);
    private static readonly Windows.UI.Color LightGray = Windows.UI.Color.FromArgb(255, 204, 204, 204);
    private static readonly Windows.UI.Color Green = Windows.UI.Color.FromArgb(255, 50, 205, 50);
    private static readonly Windows.UI.Color Red = Windows.UI.Color.FromArgb(255, 255, 68, 68);

    private static readonly BootState Connecting =
        new(">", Gray, "Connecting to SharpClaw...", LightGray, true, false);

    public async Task ConnectAsync()
    {
        IsAwaitingInput = false;
        SetState(Connecting);

        try
        {
            await _backend.EnsureStartedAsync();

            if (!_backend.IsExternal)
            {
                _api.InvalidateApiKey();
                await _api.WaitForReadyAsync(TimeSpan.FromSeconds(15));
            }

            SetState(new("✓", Green, "Connection established.", Green, false, false));
            await Task.Delay(500);

            // Skip Uno's built-in auth — navigate directly to Login.
            // The login page authenticates against the real API.
            await _navigator.NavigateRouteAsync(this, "Login", qualifier: Qualifiers.ClearBackStack);
        }
        catch (Exception ex)
        {
            IsAwaitingInput = true;
            SetState(new("✗", Red, $"Connection failed: {ex.Message}", Red, false, true));
        }
    }

    private void SetState(BootState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(this, state);
    }
}
