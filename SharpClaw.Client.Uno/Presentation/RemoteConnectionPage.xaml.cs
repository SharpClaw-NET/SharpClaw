using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010",
    Justification = "This Uno view inherits nongeneric enumeration for XAML children; it does not expose a collection API and generic enumeration would change framework semantics.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "Uno owns reusable page instances. The per-visit cancellation source is retired and disposed through awaited cancellation on unload, while each admitted service operation owns and joins its process settlement.")]
public sealed partial class RemoteConnectionPage : Page
{
    private CancellationTokenSource? _visit;
    private bool _busy;
    private bool _configured;

    private static RemoteBackendConnectionService Connection =>
        App.Services!.GetRequiredService<RemoteBackendConnectionService>();

    public RemoteConnectionPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var previous = _visit;
        var visit = new CancellationTokenSource();
        _visit = visit;
        if (previous is not null) ClientUiEvent.Observe(() => CancelAndDisposeAsync(previous));
        AccessTokenInput.Password = string.Empty;
        AuthenticationPanel.Visibility = Visibility.Collapsed;
        _busy = false;
        Cursor.SetCommand("sharpclaw remote ");
        ClientUiEvent.Observe(() => RunOperationAsync(Connection.GetStatusAsync, "Checking connection...",
            "Connection status is unavailable. Open Boot to check the local backend.", visit.Token));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var visit = _visit;
        _visit = null;
        AccessTokenInput.Password = string.Empty;
        if (visit is not null) ClientUiEvent.Observe(() => CancelAndDisposeAsync(visit));
    }

    private static async Task CancelAndDisposeAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(true); }
        finally { source.Dispose(); }
    }

    private void OnAuthenticationClick(object sender, RoutedEventArgs e)
    {
        AuthenticationPanel.Visibility = AuthenticationPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _visit is not { } visit) return;
        var address = GatewayAddressInput.Text.Trim();
        var accessToken = AccessTokenInput.Password;
        AccessTokenInput.Password = string.Empty;
        ClientUiEvent.Observe(() => RunOperationAsync(token =>
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var gatewayAddress))
                throw new FormatException("Enter an absolute remote Gateway address.");
            return Connection.ConnectAsync(gatewayAddress, accessToken, token);
        }, "Connecting...", "Connection failed. Check the Gateway address, access token and bundled local backend.", visit.Token));
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _visit is not { } visit) return;
        ClientUiEvent.Observe(() => RunOperationAsync(Connection.GetStatusAsync, "Checking connection...",
            "Connection status is unavailable. Open Boot to check the local backend.", visit.Token));
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _visit is not { } visit) return;
        AccessTokenInput.Password = string.Empty;
        ClientUiEvent.Observe(() => RunOperationAsync(Connection.DisconnectAsync, "Disconnecting...",
            "Remote connection is off. Unable to finish the local backend transition; open Boot or Settings.", visit.Token));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "The UI translates validation, action and transport failures into sanitized connection state. Only bounded exception metadata is journalled, and retired page visits cannot publish late results.")]
    private async Task RunOperationAsync(
        Func<CancellationToken, Task<RemoteBackendConnectionStatus>> operation,
        string pendingText,
        string failureText,
        CancellationToken cancellationToken)
    {
        if (_busy || cancellationToken.IsCancellationRequested) return;
        SetBusy(true);
        ConnectionStatus.Text = pendingText;
        ConnectionStatus.Foreground = TerminalUI.Brush(0xFFCC00);
        try
        {
            var status = await operation(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            ShowStatus(status);
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            if (cancellationToken.IsCancellationRequested) return;
            ShowFailedStatus(failureText);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) SetBusy(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "A failed UI operation may include unreadable protected configuration. This final UI boundary retains an explicit unavailable state and an explicit disconnect recovery action without exposing configuration or exception messages.")]
    private void ShowFailedStatus(string failureText)
    {
        try
        {
            var configured = Connection.GetConfiguredStatus();
            if (configured.IsConfigured)
            {
                _configured = true;
                if (configured.GatewayAddress is { } address) GatewayAddressInput.Text = address.AbsoluteUri;
                ConnectionStatus.Text = "Connection change failed. Remote backend remains configured; refresh to check availability.";
                ConnectionStatus.Foreground = TerminalUI.Brush(0xFF4444);
                return;
            }
            _configured = false;
            ConnectionStatus.Text = failureText;
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            _configured = true;
            ConnectionStatus.Text = "Connection configuration is unavailable. Disconnect to return to local mode.";
        }
        ConnectionStatus.Foreground = TerminalUI.Brush(0xFF4444);
    }

    private void ShowStatus(RemoteBackendConnectionStatus status)
    {
        _configured = status.IsConfigured;
        if (status.GatewayAddress is { } address) GatewayAddressInput.Text = address.AbsoluteUri;
        ConnectionStatus.Text = status.IsConnected ? "Connected through the local backend." :
            status.IsConfigured ? "Configured. Remote Gateway unavailable." : "Not connected. Local modules are available from Boot.";
        ConnectionStatus.Foreground = TerminalUI.Brush(status.IsConnected ? 0x00FF00 : status.HasError ? 0xFF4444 : 0x808080);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        GatewayAddressInput.IsEnabled = !busy;
        AuthenticationButton.IsEnabled = !busy;
        AccessTokenInput.IsEnabled = !busy;
        ConnectButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        DisconnectButton.IsEnabled = !busy && _configured;
    }

    private void OnBootClick(object sender, RoutedEventArgs e) => Navigate("Boot");
    private void OnSettingsClick(object sender, RoutedEventArgs e) => Navigate("Settings");
    private void OnChatClick(object sender, RoutedEventArgs e) => Navigate("Main");

    private void Navigate(string route) => ClientUiEvent.Observe(async () =>
    {
        await App.Services!.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, route, cancellationToken: CancellationToken.None).ConfigureAwait(true);
    });
}
