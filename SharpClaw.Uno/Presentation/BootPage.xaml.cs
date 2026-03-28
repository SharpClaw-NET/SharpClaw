using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class BootPage : Page
{
    private static readonly string[] DotsFrames = [".", "..", "..."];

    private static readonly Windows.UI.Color GreenColor = Windows.UI.Color.FromArgb(255, 50, 205, 50);
    private static readonly Windows.UI.Color RedColor = Windows.UI.Color.FromArgb(255, 255, 68, 68);
    private static readonly Windows.UI.Color GrayColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
    private static readonly Windows.UI.Color LightGrayColor = Windows.UI.Color.FromArgb(255, 204, 204, 204);
    private static readonly Windows.UI.Color LightRedColor = Windows.UI.Color.FromArgb(255, 255, 120, 120);

    public BootPage()
    {
        this.InitializeComponent();
        KeyDown += OnKeyDown;
        Tapped += OnPageTapped;

        _dotsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dotsTimer.Tick += (_, _) =>
        {
            _dotsFrame = (_dotsFrame + 1) % DotsFrames.Length;
            if (_activeDots is not null)
                _activeDots.Text = DotsFrames[_dotsFrame];
        };
    }

    private BootModel? _model;
    private readonly DispatcherTimer _dotsTimer;
    private int _dotsFrame;
    private TextBlock? _activeDots;
    private CancellationTokenSource? _retryCts;
    private ImmutableArray<DiagnosticLine> _lastDiag;

    private static readonly JsonSerializerOptions _autoLoginJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var services = App.Services;
        _model ??= new BootModel(
            services.GetRequiredService<BackendProcessManager>(),
            services.GetRequiredService<GatewayProcessManager>(),
            services.GetRequiredService<SharpClawApiClient>());

        // Cancel any in-flight connection attempt from a previous visit.
        _retryCts?.Cancel();
        _retryCts?.Dispose();

        ResetAllVisuals();
        this.Focus(FocusState.Programmatic);

        _retryCts = new CancellationTokenSource();
        await RunConnectionFlowAsync(customUrl: null, _retryCts.Token);
    }

    // ---------------------------------------------------------------
    // Main connection flow — page drives everything sequentially
    // ---------------------------------------------------------------
    private async Task RunConnectionFlowAsync(string? customUrl, CancellationToken ct)
    {
        _model!.ApplyCustomUrl(customUrl);
        _model.IsAwaitingInput = false;
        var diag = ImmutableArray.CreateBuilder<DiagnosticLine>();

        for (int attempt = 1; attempt <= BootModel.MaxRetries; attempt++)
        {
            if (ct.IsCancellationRequested)
                break;

            diag.Clear();
            ResetAllVisuals();

            // -- Retry label on subsequent attempts --
            if (attempt > 1)
            {
                Cursor.SetCommand($"Retrying ({attempt}/{BootModel.MaxRetries})...");
                await Task.Delay(600, ct);
                Cursor.ClearCommand();
            }

            // -- Step 1: Backend (silent) --
            var backendResult = await _model.RunBackendStepAsync(ct);
            diag.Add(backendResult.Line);

            if (!backendResult.Ok)
            {
                ShowFailure(diag.ToImmutable());
                if (attempt < BootModel.MaxRetries)
                {
                    await RetryPauseAsync(attempt, diag.ToImmutable(), ct);
                    continue;
                }
                break;
            }

            // -- Step 2: Type "sharpclaw echo" → run echo probe --
            await Cursor.TypeCommandAsync("sharpclaw echo");
            StartDots(DotsBlock);

            var echoResult = await _model.RunEchoStepAsync(ct);
            diag.Add(echoResult.Line);

            StopDots();
            ShowStepResult(EchoResultPanel, EchoIconBlock, EchoTextBlock, echoResult);

            if (!echoResult.Ok)
            {
                if (attempt < BootModel.MaxRetries)
                {
                    await RetryPauseAsync(attempt, diag.ToImmutable(), ct);
                    continue;
                }
                break;
            }

            // -- Step 3: Type "sharpclaw ping" → run ping probe --
            Cursor.Freeze();
            PingCursor.Visibility = Visibility.Visible;
            await PingCursor.TypeCommandAsync("sharpclaw ping");
            StartDots(PingDotsBlock);

            var (pingResult, apiKeyLine) = await _model.RunPingStepAsync(ct);
            if (apiKeyLine is not null) diag.Add(apiKeyLine);
            diag.Add(pingResult.Line);

            StopDots();
            ShowStepResult(StatusPanel, StatusIconBlock, StatusTextBlock, pingResult);

            if (pingResult.Ok)
            {
                // Optional: start the public gateway (non-blocking, non-fatal).
                var gatewayResult = await _model.RunGatewayStepAsync(ct);
                if (gatewayResult is not null)
                    diag.Add(gatewayResult.Line);

                // Try auto-login from saved account before showing Login page
                if (await TryAutoLoginAsync(ct))
                    return;

                // No auto-login — navigate to login page
                await Task.Delay(1000, CancellationToken.None);
                var navigator = App.Services!.GetRequiredService<INavigator>();
                await navigator.NavigateRouteAsync(this, "Login", qualifier: Qualifiers.ClearBackStack);
                return;
            }

            if (attempt < BootModel.MaxRetries)
            {
                await RetryPauseAsync(attempt, diag.ToImmutable(), ct);
                continue;
            }
        }

        // -- All attempts exhausted or cancelled --
        _model.IsAwaitingInput = true;

        var finalDiag = diag.ToImmutable();
        if (ct.IsCancellationRequested)
        {
            ShowFinalStatus("—", GrayColor, "Connection cancelled.", LightGrayColor);
        }
        else
        {
            ShowFinalStatus("✗", RedColor,
                BootModel.SummariseDiagnostic(finalDiag), RedColor);
        }

        PopulateDiagnostics(finalDiag);
        RetryPromptBlock.Visibility = Visibility.Visible;
        UrlPanel.Visibility = Visibility.Visible;
        UrlBox.Text = _model.ApiUrl.TrimEnd('/');
        this.Focus(FocusState.Programmatic);
    }

    // ---------------------------------------------------------------
    // UI helpers
    // ---------------------------------------------------------------
    private static void ShowStepResult(
        StackPanel panel, TextBlock iconBlock, TextBlock textBlock, StepResult result)
    {
        iconBlock.Text = result.Ok ? "✓" : "✗";
        iconBlock.Foreground = BrushFrom(result.Ok ? GreenColor : RedColor);
        textBlock.Text = result.Line.Result;
        textBlock.Foreground = BrushFrom(result.Ok ? LightGrayColor : LightRedColor);
        panel.Visibility = Visibility.Visible;
    }

    private void ShowFinalStatus(
        string icon, Windows.UI.Color iconColor, string text, Windows.UI.Color textColor)
    {
        StatusIconBlock.Text = icon;
        StatusIconBlock.Foreground = BrushFrom(iconColor);
        StatusTextBlock.Text = text;
        StatusTextBlock.Foreground = BrushFrom(textColor);
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void ShowFailure(ImmutableArray<DiagnosticLine> diag)
    {
        var summary = BootModel.SummariseDiagnostic(diag);
        ShowFinalStatus("✗", RedColor, summary, RedColor);
        PopulateDiagnostics(diag);
    }

    private async Task RetryPauseAsync(
        int attempt, ImmutableArray<DiagnosticLine> diag, CancellationToken ct)
    {
        PopulateDiagnostics(diag);
        var msg = $"Attempt {attempt} of {BootModel.MaxRetries} failed. Retrying in {(int)BootModel.RetryDelay.TotalSeconds}s...";
        ShowFinalStatus("⟳", GrayColor, msg, LightGrayColor);

        try { await Task.Delay(BootModel.RetryDelay, ct); }
        catch (OperationCanceledException) { /* caller checks ct */ }
    }

    // ---------------------------------------------------------------
    // Dots animation helpers
    // ---------------------------------------------------------------
    private void StartDots(TextBlock target)
    {
        _activeDots = target;
        _dotsFrame = 0;
        target.Text = DotsFrames[0];
        target.Visibility = Visibility.Visible;
        _dotsTimer.Start();
    }

    private void StopDots()
    {
        _dotsTimer.Stop();
        DotsBlock.Visibility = Visibility.Collapsed;
        PingDotsBlock.Visibility = Visibility.Collapsed;
        _activeDots = null;
    }

    // ---------------------------------------------------------------
    // Reset all visuals for a fresh attempt
    // ---------------------------------------------------------------
    private void ResetAllVisuals()
    {
        StopDots();
        Cursor.Unfreeze();
        Cursor.ClearCommand();
        PingCursor.Unfreeze();
        PingCursor.ClearCommand();
        EchoResultPanel.Visibility = Visibility.Collapsed;
        PingCursor.Visibility = Visibility.Collapsed;
        PingDotsBlock.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        DiagPanel.Visibility = Visibility.Collapsed;
        ProcessOutputPanel.Visibility = Visibility.Collapsed;
        RetryPromptBlock.Visibility = Visibility.Collapsed;
        UrlPanel.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------
    // Diagnostic log panel
    // ---------------------------------------------------------------
    private void PopulateDiagnostics(ImmutableArray<DiagnosticLine> log)
    {
        DiagLines.Children.Clear();
        _lastDiag = log;

        // Reset copy button label
        CopyLogsLabel.Text = "Copy";

        if (log.IsDefaultOrEmpty)
        {
            DiagPanel.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var entry in log)
        {
            var icon = new TextBlock
            {
                Text = entry.IsError ? "✗" : "✓",
                FontSize = 12,
                Foreground = BrushFrom(entry.IsError ? RedColor : GreenColor),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var label = new TextBlock
            {
                Text = entry.Label,
                FontSize = 12,
                Foreground = BrushFrom(GrayColor),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var result = new TextBlock
            {
                Text = entry.Result,
                FontSize = 12,
                Foreground = BrushFrom(entry.IsError ? LightRedColor : LightGrayColor),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
            };

            if (Resources.TryGetValue("TerminalText", out var style)
                || Application.Current.Resources.TryGetValue("TerminalText", out style))
            {
                if (style is Style textStyle)
                {
                    icon.Style = textStyle;
                    label.Style = textStyle;
                    result.Style = textStyle;
                    icon.FontSize = 12;
                    label.FontSize = 12;
                    result.FontSize = 12;
                    icon.Foreground = BrushFrom(entry.IsError ? RedColor : GreenColor);
                    label.Foreground = BrushFrom(GrayColor);
                    result.Foreground = BrushFrom(entry.IsError ? LightRedColor : LightGrayColor);
                    result.TextWrapping = TextWrapping.Wrap;
                    result.MaxWidth = 360;
                }
            }

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };
            row.Children.Add(icon);
            row.Children.Add(label);
            row.Children.Add(result);
            DiagLines.Children.Add(row);
        }

        // Show backend process output if available
        var backend = App.Services.GetRequiredService<BackendProcessManager>();
        var output = backend.ProcessOutput;
        if (output.Count > 0)
        {
            ProcessOutputBlock.Text = string.Join(Environment.NewLine, output);
            ProcessOutputPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ProcessOutputPanel.Visibility = Visibility.Collapsed;
        }

        DiagPanel.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush BrushFrom(Windows.UI.Color color) => new(color);

    // ---------------------------------------------------------------
    // Clipboard
    // ---------------------------------------------------------------
    private async void OnCopyLogsClick(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;

        var report = _model.BuildDiagnosticReport(_lastDiag);
        var dp = new DataPackage();
        dp.SetText(report);
        Clipboard.SetContent(dp);

        CopyLogsLabel.Text = "Copied!";
        await Task.Delay(2000);
        CopyLogsLabel.Text = "Copy";
    }

    // ---------------------------------------------------------------
    // Keyboard / input
    // ---------------------------------------------------------------
    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;

            if (_retryCts is { IsCancellationRequested: false })
            {
                _retryCts.Cancel();
                return;
            }

            if (_model is { IsAwaitingInput: true })
                ((App)Application.Current).MainWindow?.Close();

            return;
        }

        if (_model is not { IsAwaitingInput: true })
            return;

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            var url = UrlBox.Text?.Trim();
            _retryCts?.Dispose();
            _retryCts = new CancellationTokenSource();
            await RunConnectionFlowAsync(url, _retryCts.Token);
        }
    }

    private void OnPageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_model is { IsAwaitingInput: true })
            this.Focus(FocusState.Programmatic);
    }

    // ---------------------------------------------------------------
    // Auto-login from saved account
    // ---------------------------------------------------------------
    private async Task<bool> TryAutoLoginAsync(CancellationToken ct)
    {
        var store = App.Services?.GetService<AccountStore>();
        var account = store?.GetActiveAccount();
        if (account is null || !account.RememberMe
            || account.RefreshToken is null
            || account.RefreshTokenExpiresAt <= DateTimeOffset.UtcNow)
            return false;

        try
        {
            var api = App.Services!.GetRequiredService<SharpClawApiClient>();
            var body = JsonSerializer.Serialize(
                new { refreshToken = account.RefreshToken }, _autoLoginJson);
            using var resp = await api.PostAsync("/auth/refresh",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);

            if (!resp.IsSuccessStatusCode)
                return false;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var accessToken = root.TryGetProperty("accessToken", out var atProp)
                ? atProp.GetString() : null;
            if (accessToken is null) return false;

            api.SetAccessToken(accessToken);

            // Update stored tokens with fresh values
            account.AccessToken = accessToken;
            if (root.TryGetProperty("accessTokenExpiresAt", out var atExp))
                account.AccessTokenExpiresAt = atExp.GetDateTimeOffset();
            if (root.TryGetProperty("refreshToken", out var rt) && rt.ValueKind == JsonValueKind.String)
                account.RefreshToken = rt.GetString();
            if (root.TryGetProperty("refreshTokenExpiresAt", out var rtExp) && rtExp.ValueKind != JsonValueKind.Null)
                account.RefreshTokenExpiresAt = rtExp.GetDateTimeOffset();
            store!.SaveAccount(account);

            // Switch per-user settings
            App.Services!.GetRequiredService<ClientSettings>().SwitchUser(account.UserId);

            await Task.Delay(1000, CancellationToken.None);
            var needsSetup = !FirstSetupMarker.IsCompleted;
            var needsUpgrade = !needsSetup && FirstSetupMarker.NeedsUpgradeRerun;
            var target = needsSetup || needsUpgrade ? "FirstSetup" : "Main";
            var navigator = App.Services!.GetRequiredService<INavigator>();
            await navigator.NavigateRouteAsync(this, target, qualifier: Qualifiers.ClearBackStack);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnEnvClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        EnvMenuPage.PendingOrigin = "Boot";
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "EnvMenu");
    }
}
