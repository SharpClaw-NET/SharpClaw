using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class BootPage : Page
{
    public BootPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    private BootModel? _model;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_model is not null) return;

        var services = App.Services;
        _model = new BootModel(
            services.GetRequiredService<BackendProcessManager>(),
            services.GetRequiredService<SharpClawApiClient>(),
            services.GetRequiredService<INavigator>(),
            services);

        _model.StateChanged += OnModelStateChanged;
        ApplyState(_model.CurrentState);
        await _model.ConnectAsync();
    }

    private void OnModelStateChanged(object? sender, BootState state) =>
        DispatcherQueue.TryEnqueue(() => ApplyState(state));

    private void ApplyState(BootState state)
    {
        StatusIconBlock.Text = state.Icon;
        StatusIconBlock.Foreground = BrushFrom(state.IconColor);
        StatusTextBlock.Text = state.Text;
        StatusTextBlock.Foreground = BrushFrom(state.TextColor);
        SpinnerRing.IsActive = state.IsSpinnerActive;
        SpinnerRing.Visibility = state.IsSpinnerActive ? Visibility.Visible : Visibility.Collapsed;
        RetryPromptBlock.Visibility = state.IsRetryVisible ? Visibility.Visible : Visibility.Collapsed;
        CursorBlock.Visibility = state.IsRetryVisible ? Visibility.Visible : Visibility.Collapsed;

        if (state.IsRetryVisible)
            Focus(FocusState.Programmatic);
    }

    private static SolidColorBrush BrushFrom(Windows.UI.Color color) => new(color);

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_model is not { IsAwaitingInput: true })
            return;

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await _model.ConnectAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Application.Current.Exit();
        }
    }
}
