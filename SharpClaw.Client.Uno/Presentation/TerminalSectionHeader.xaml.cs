namespace SharpClaw.Presentation;

public sealed partial class TerminalSectionHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(TerminalSectionHeader),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public TerminalSectionHeader()
    {
        this.InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TerminalSectionHeader self)
            self.HeaderBlock.Text = $"── {(string)e.NewValue} ──";
    }
}
