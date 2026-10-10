using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SharpClaw.Presentation;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010",
    Justification = "This Uno view inherits nongeneric enumeration from the framework for XAML children; it is not a public collection API and adding generic enumeration would change framework semantics.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "Uno owns reusable page instances. RetireBootWork cancels/disposes per-visit retry and inspection sources and stops timers; the reusable connection gate never allocates AvailableWaitHandle and releases acquisitions in finally.")]
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
        Loaded += (_, _) => ClientStartupDiagnostics.Current.Record(ClientStartupStage.BootLoaded);
        Unloaded += (_, _) => RetireBootWork();
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
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private bool _isActive;
    private readonly DispatcherTimer _dotsTimer;
    private int _dotsFrame;
    private TextBlock? _activeDots;
    private CancellationTokenSource? _retryCts;
    private ImmutableArray<DiagnosticLine> _lastDiag;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    protected override void OnNavigatedTo(NavigationEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        base.OnNavigatedTo(e);
        _isActive = true;

        var services = App.Services!;
        _model ??= new BootModel(
            services.GetRequiredService<BackendProcessManager>(),
            services.GetRequiredService<GatewayProcessManager>(),
            services.GetRequiredService<SharpClawApiClient>(),
            services.GetRequiredService<FrontendInstanceService>(),
            services.GetRequiredService<ClientActionDispatcher>());
        _model.IsAwaitingInput = false;

        // Publish this visit before cancellation can suspend and navigation can retire it.
        var previous = _retryCts;
        var visit = new CancellationTokenSource();
        _retryCts = visit;
        var token = visit.Token;
        if (previous is not null)
        {
            try { await previous.CancelAsync().ConfigureAwait(true); }
            finally { previous.Dispose(); }
        }
        token.ThrowIfCancellationRequested();
        ResetAllVisuals();
        this.Focus(FocusState.Programmatic);
        var backend = services.GetRequiredService<BackendProcessManager>();
        if (backend.IsAvailable && !backend.SkipLaunch && !backend.IsExternal &&
            Uri.TryCreate(backend.ApiUrl, UriKind.Absolute, out var target) && target.IsLoopback)
        {
            try
            {
                var installed = services.GetRequiredService<ModulePackageStore>().ReadInstalled();
                var bundled = ModulePackageStore.ReadIdentities(
                    Path.Combine(Path.GetDirectoryName(backend.ExecutablePath)!, "contributions"), true);
                var frontend = services.GetRequiredService<FrontendInstanceService>();
                if (!installed.Concat(bundled).Any(module => BundledModuleSetup.IsEnabled(frontend, module.Id, module.DefaultEnabled)))
                {
                    Cursor.SetCommand("No modules enabled. Install modules or open Settings.");
                    return;
                }
            }
            catch (Exception exception)
            {
                ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
                Cursor.SetCommand("Module configuration unavailable. Open Settings or install modules.");
                return;
            }
        }
        await RunConnectionFlowAsync(customUrl: null, token).ConfigureAwait(true);
    });

    // ---------------------------------------------------------------
    // Main connection flow — page drives everything sequentially
    // ---------------------------------------------------------------
    private async Task RunConnectionFlowAsync(string? customUrl, CancellationToken ct)
    {
        try
        {
            await _connectionGate.WaitAsync(ct).ConfigureAwait(true);
            try { await RunConnectionCoreAsync(customUrl, ct).ConfigureAwait(true); }
            finally { _connectionGate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    private async Task RunConnectionCoreAsync(string? customUrl, CancellationToken ct)
    {
        _model!.IsAwaitingInput = false;
        var diag = ImmutableArray.CreateBuilder<DiagnosticLine>();

        try
        {
            await _model.ApplyCustomUrlAsync(customUrl, ct).ConfigureAwait(true);
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
                    await Task.Delay(600, ct).ConfigureAwait(true);
                    Cursor.ClearCommand();
                }

                var attemptResult = await RunBootAttemptAsync(diag, ct).ConfigureAwait(true);
                if (attemptResult.Result.Ok) return;
                if (_model.ShouldRetry(attemptResult.Result, attempt))
                    await RetryPauseAsync(attempt, diag.ToImmutable(), ct).ConfigureAwait(true);
                else if (!attemptResult.ReachedPing) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            diag.Add(new DiagnosticLine("Connection", "Connection failed or was denied. Check the service configuration and diagnostics.", true));
        }
        finally { StopDots(); }

        ShowStoppedConnection(diag.ToImmutable(), ct);
    }

    private void ShowStoppedConnection(ImmutableArray<DiagnosticLine> log, CancellationToken ct)
    {
        if (!_isActive) return;

        // -- All attempts exhausted or cancelled --
        StopDots();
        Cursor.Freeze();
        PingCursor.Freeze();
        _model!.IsAwaitingInput = true;

        var finalDiag = _model.RefreshBackendDiagnostic(log);
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

    private async Task<(StepResult Result, bool ReachedPing)> RunBootAttemptAsync(
        ImmutableArray<DiagnosticLine>.Builder diag, CancellationToken ct)
    {
        var backendResult = await _model!.RunBackendStepAsync(ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        diag.Add(backendResult.Line);
        if (!backendResult.Ok) { ShowFailure(diag.ToImmutable()); return (backendResult, false); }
        await Cursor.TypeCommandAsync("sharpclaw echo").ConfigureAwait(true);
        StartDots(DotsBlock);
        var echoResult = await _model.RunEchoStepAsync(ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        diag.Add(echoResult.Line);
        StopDots();
        ShowStepResult(EchoResultPanel, EchoIconBlock, EchoTextBlock, echoResult);
        if (!echoResult.Ok) return (echoResult, false);
        Cursor.Freeze();
        PingCursor.Visibility = Visibility.Visible;
        await PingCursor.TypeCommandAsync("sharpclaw ping").ConfigureAwait(true);
        StartDots(PingDotsBlock);
        var (pingResult, apiKeyLine) = await _model.RunPingStepAsync(ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        if (apiKeyLine is not null) diag.Add(apiKeyLine);
        diag.Add(pingResult.Line);
        StopDots();
        ShowStepResult(StatusPanel, StatusIconBlock, StatusTextBlock, pingResult);
        if (pingResult.Ok)
        {
            var gatewayResult = await _model.RunGatewayStepAsync(ct).ConfigureAwait(true);
            if (gatewayResult is not null) diag.Add(gatewayResult.Line);
            _model.IsAwaitingInput = false;
        }
        return (pingResult, true);
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

        try { await Task.Delay(BootModel.RetryDelay, ct).ConfigureAwait(true); }
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
        DotsBlock.Text = string.Empty;
        PingDotsBlock.Text = string.Empty;
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

        foreach (var entry in log) DiagLines.Children.Add(CreateDiagnosticRow(entry));

        // Show backend process output if available
        var backend = App.Services!.GetRequiredService<BackendProcessManager>();
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

    private StackPanel CreateDiagnosticRow(DiagnosticLine entry)
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

        ApplyDiagnosticStyle(icon, label, result, entry);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        row.Children.Add(icon);
        row.Children.Add(label);
        row.Children.Add(result);
        return row;
    }

    private void ApplyDiagnosticStyle(TextBlock icon, TextBlock label, TextBlock result, DiagnosticLine entry)
    {
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

    }

    private static SolidColorBrush BrushFrom(Windows.UI.Color color) => new(color);

    // ---------------------------------------------------------------
    // Clipboard
    // ---------------------------------------------------------------
    private void OnCopyLogsClick(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (_model is null) return;

        var report = _model.BuildDiagnosticReport(_lastDiag);
        var dp = new DataPackage();
        dp.SetText(report);
        Clipboard.SetContent(dp);

        CopyLogsLabel.Text = "Copied!";
        await Task.Delay(2000, _retryCts?.Token ?? CancellationToken.None).ConfigureAwait(true);
        CopyLogsLabel.Text = "Copy";
    });

    // ---------------------------------------------------------------
    // Keyboard / input
    // ---------------------------------------------------------------
    private void OnKeyDown(object sender, KeyRoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (e.OriginalSource is TextBox or PasswordBox or ComboBox or Button) return;
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;

            if (_model is { IsAwaitingInput: true })
            {
                ((App)Application.Current).MainWindow?.Close();
                return;
            }

            if (_retryCts is { IsCancellationRequested: false })
            {
                await _retryCts.CancelAsync().ConfigureAwait(true);
                return;
            }

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
            await RunConnectionFlowAsync(url, _retryCts.Token).ConfigureAwait(true);
        }
    });

    private void OnPageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ModuleInstallPanel.Visibility != Visibility.Visible && _model is { IsAwaitingInput: true })
            this.Focus(FocusState.Programmatic);
    }

}
