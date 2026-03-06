using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class BootPage : Page
{
    private static readonly string[] DotsFrames = [".", "..", "..."];

    public BootPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;

        _dotsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dotsTimer.Tick += (_, _) =>
        {
            _dotsFrame = (_dotsFrame + 1) % DotsFrames.Length;
            DotsBlock.Text = DotsFrames[_dotsFrame];
        };
    }

    private BootModel? _model;
    private Task _typewriterDone = Task.CompletedTask;
    private BootState? _pendingResultState;
    private readonly DispatcherTimer _dotsTimer;
    private int _dotsFrame;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_model is not null) return;

        var services = App.Services;
        _model = new BootModel(
            services.GetRequiredService<BackendProcessManager>(),
            services.GetRequiredService<SharpClawApiClient>());

        _model.StateChanged += OnModelStateChanged;
        _model.NavigateRequested += OnNavigateRequested;
        ApplyState(_model.CurrentState);
        await _model.ConnectAsync();
    }

    private void OnModelStateChanged(object? sender, BootState state) =>
        DispatcherQueue.TryEnqueue(() => ApplyState(state));

    private void ApplyState(BootState state)
    {
        if (state.IsSpinnerActive)
        {
            // Connecting — start typewriter, hide result, dots start after typing
            _pendingResultState = null;
            StatusPanel.Visibility = Visibility.Collapsed;
            DotsBlock.Visibility = Visibility.Collapsed;
            RetryPromptBlock.Visibility = Visibility.Collapsed;
            UrlPanel.Visibility = Visibility.Collapsed;
            StopDots();

            _typewriterDone = Cursor.TypeCommandAsync("sharpclaw ping");
            _typewriterDone.ContinueWith(_ =>
                DispatcherQueue.TryEnqueue(StartDots),
                TaskScheduler.Default);
            return;
        }

        // Result state — defer until typewriter finishes
        if (!_typewriterDone.IsCompleted)
        {
            _pendingResultState = state;
            _typewriterDone.ContinueWith(_ =>
                DispatcherQueue.TryEnqueue(FlushPendingResult),
                TaskScheduler.Default);
            return;
        }

        ApplyResultState(state);
    }

    private void StartDots()
    {
        // Only start if we haven't already transitioned to a result
        if (_pendingResultState is not null)
            return;

        _dotsFrame = 0;
        DotsBlock.Text = DotsFrames[0];
        DotsBlock.Visibility = Visibility.Visible;
        _dotsTimer.Start();
    }

    private void StopDots()
    {
        _dotsTimer.Stop();
        DotsBlock.Visibility = Visibility.Collapsed;
    }

    private void FlushPendingResult()
    {
        if (_pendingResultState is { } state)
        {
            _pendingResultState = null;
            ApplyResultState(state);
        }
    }

    private async void OnNavigateRequested(object? sender, EventArgs e)
    {
        // Ensure the typewriter finishes before showing the result.
        await _typewriterDone;
        DispatcherQueue.TryEnqueue(() => FlushPendingResult());

        // Let the user see "Connection established." for a moment.
        await Task.Delay(1000);

        var navigator = App.Services!.GetRequiredService<INavigator>();
        await navigator.NavigateRouteAsync(this, "Login", qualifier: Qualifiers.ClearBackStack);
    }

    private void ApplyResultState(BootState state)
    {
        StopDots();

        StatusIconBlock.Text = state.Icon;
        StatusIconBlock.Foreground = BrushFrom(state.IconColor);
        StatusTextBlock.Text = state.Text;
        StatusTextBlock.Foreground = BrushFrom(state.TextColor);
        StatusPanel.Visibility = Visibility.Visible;

        RetryPromptBlock.Visibility = state.IsRetryVisible ? Visibility.Visible : Visibility.Collapsed;
        UrlPanel.Visibility = state.IsRetryVisible ? Visibility.Visible : Visibility.Collapsed;

        if (state.IsRetryVisible && _model is not null)
        {
            UrlBox.Text = _model.ApiUrl.TrimEnd('/');
            UrlBox.Focus(FocusState.Programmatic);
        }
    }

    private static SolidColorBrush BrushFrom(Windows.UI.Color color) => new(color);

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_model is not { IsAwaitingInput: true })
            return;

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            var url = UrlBox.Text?.Trim();
            await _model.ConnectAsync(url);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            ((App)Application.Current).MainWindow?.Close();
        }
    }
}
