namespace SharpClaw.Presentation;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010",
    Justification = "This Uno view inherits nongeneric enumeration from the framework for XAML children; it is not a public collection API and adding generic enumeration would change framework semantics.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515",
    Justification = "The net10.0 headless target exposes this public TerminalSectionHeader XAML type and its framework constructor/binding contracts. The Uno desktop target shares its declaration; changing accessibility would split the existing source/binary contract across these targets.")]
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
