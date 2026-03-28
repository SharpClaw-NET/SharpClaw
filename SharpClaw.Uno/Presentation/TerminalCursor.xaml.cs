namespace SharpClaw.Presentation;

public sealed partial class TerminalCursor : UserControl
{
    private readonly DispatcherTimer _blinkTimer;
    private DispatcherTimer? _typewriterTimer;
    private string _targetText = string.Empty;
    private int _typeIndex;
    private bool _cursorVisible = true;
    private TaskCompletionSource? _typeTcs;

    private bool _frozen;

    public TerminalCursor()
    {
        this.InitializeComponent();

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) =>
        {
            _cursorVisible = !_cursorVisible;
            BlinkBlock.Text = _cursorVisible ? "_" : " ";
        };
        _blinkTimer.Start();
    }

    /// <summary>
    /// Sets the command text instantly (no animation).
    /// </summary>
    public void SetCommand(string text)
    {
        StopTypewriter();
        CommandBlock.Text = text;
    }

    /// <summary>
    /// Clears the command text, leaving only the blinking cursor.
    /// </summary>
    public void ClearCommand()
    {
        StopTypewriter();
        CommandBlock.Text = string.Empty;
    }

    /// <summary>
    /// Types out the command text character-by-character.
    /// </summary>
    public void TypeCommand(string text)
    {
        StopTypewriter();
        _targetText = text;
        _typeIndex = 0;
        CommandBlock.Text = string.Empty;
        _typeTcs = new TaskCompletionSource();

        _typewriterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(23) };
        _typewriterTimer.Tick += OnTypewriterTick;
        _typewriterTimer.Start();
    }

    /// <summary>
    /// Types out the command text and returns a <see cref="Task"/> that
    /// completes when the animation finishes.
    /// </summary>
    public Task TypeCommandAsync(string text)
    {
        TypeCommand(text);
        return _typeTcs?.Task ?? Task.CompletedTask;
    }

    private void OnTypewriterTick(object? sender, object e)
    {
        if (_typeIndex < _targetText.Length)
        {
            _typeIndex++;
            CommandBlock.Text = _targetText[.._typeIndex];
        }
        else
        {
            _typewriterTimer?.Stop();
            _typeTcs?.TrySetResult();
        }
    }

    /// <summary>
    /// Stops the blinking cursor and hides the caret.
    /// The command text is preserved.
    /// </summary>
    public void Freeze()
    {
        _frozen = true;
        _blinkTimer.Stop();
        BlinkBlock.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Resumes blinking after a previous <see cref="Freeze"/> call.
    /// </summary>
    public void Unfreeze()
    {
        _frozen = false;
        BlinkBlock.Visibility = Visibility.Visible;
        BlinkBlock.Text = "_";
        _cursorVisible = true;
        _blinkTimer.Start();
    }

    private void StopTypewriter()
    {
        _typewriterTimer?.Stop();
        _typewriterTimer = null;
        _targetText = string.Empty;
        _typeIndex = 0;
        _typeTcs?.TrySetResult();
        _typeTcs = null;
    }
}
