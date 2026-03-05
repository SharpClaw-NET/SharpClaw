using Microsoft.UI.Xaml.Input;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _cursorBlinkTimer;
    private DispatcherTimer? _typewriterTimer;
    private string _targetCommand = string.Empty;
    private int _typeIndex;
    private bool _cursorVisible = true;

    private static readonly Dictionary<string, string> CliCommands = new()
    {
        ["Providers"]     = "sharpclaw provider list",
        ["Models"]        = "sharpclaw model list",
        ["LocalModels"]   = "sharpclaw local-model list",
        ["Agents"]        = "sharpclaw agent list",
        ["Roles"]         = "sharpclaw role list",
        ["Jobs"]          = "sharpclaw job list",
        ["Channels"]      = "sharpclaw channel list",
        ["Contexts"]      = "sharpclaw ctx list",
        ["Resources"]     = "sharpclaw resource list",
        ["Editor"]        = "sharpclaw editor list",
        ["Transcription"] = "sharpclaw transcribe start",
        ["Auth"]          = "sharpclaw auth status",
    };

    public MainPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;

        _cursorBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _cursorBlinkTimer.Tick += OnCursorBlink;
        _cursorBlinkTimer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;

        var api = services.GetRequiredService<SharpClawApiClient>();
        StatusBlock.Text = $"connected to {api.BaseUrl}";
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            StatusBlock.Text = $"> {tag} (coming soon)";
        }
    }

    private void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;

        var api = services.GetRequiredService<SharpClawApiClient>();
        api.SetAccessToken(null!);

        var navigator = services.GetRequiredService<INavigator>();
        _ = navigator.NavigateRouteAsync(this, "Login", qualifier: Qualifiers.ClearBackStack);
    }

    private void OnMenuPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && CliCommands.TryGetValue(tag, out var command))
            StartTypewriter(command);
    }

    private void OnMenuPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ClearTypewriter();
    }

    private void StartTypewriter(string command)
    {
        _typewriterTimer?.Stop();
        _targetCommand = command;
        _typeIndex = 0;
        CursorCommandBlock.Text = string.Empty;

        _typewriterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _typewriterTimer.Tick += OnTypewriterTick;
        _typewriterTimer.Start();
    }

    private void OnTypewriterTick(object? sender, object e)
    {
        if (_typeIndex < _targetCommand.Length)
        {
            _typeIndex++;
            CursorCommandBlock.Text = _targetCommand[.._typeIndex];
        }
        else
        {
            _typewriterTimer?.Stop();
        }
    }

    private void ClearTypewriter()
    {
        _typewriterTimer?.Stop();
        _typewriterTimer = null;
        _targetCommand = string.Empty;
        _typeIndex = 0;
        CursorCommandBlock.Text = string.Empty;
    }

    private void OnCursorBlink(object? sender, object e)
    {
        _cursorVisible = !_cursorVisible;
        CursorBlock.Text = _cursorVisible ? "_" : " ";
    }
}
