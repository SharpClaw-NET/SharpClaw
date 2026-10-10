using Microsoft.UI.Xaml.Input;

namespace SharpClaw.Presentation;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010",
    Justification = "This Uno view inherits nongeneric enumeration from the framework for XAML children; it is not a public collection API and adding generic enumeration would change framework semantics.")]
public sealed partial class TerminalMenuButton : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(TerminalMenuButton),
            new PropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(TerminalMenuButton),
            new PropertyMetadata(string.Empty, OnDescriptionChanged));

    public static readonly DependencyProperty TagKeyProperty =
        DependencyProperty.Register(nameof(TagKey), typeof(string), typeof(TerminalMenuButton),
            new PropertyMetadata(string.Empty));

    public TerminalMenuButton()
    {
        this.InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string TagKey
    {
        get => (string)GetValue(TagKeyProperty);
        set => SetValue(TagKeyProperty, value);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1003",
        Justification = "This existing XAML control event publishes the TagKey string directly; changing its delegate argument to EventArgs would break current public handler contracts.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0046",
        Justification = "This existing XAML control event publishes the TagKey string directly; changing its delegate argument to EventArgs would break current public handler contracts.")]
    public event EventHandler<string>? MenuClick;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1003",
        Justification = "This existing XAML control event publishes the TagKey string directly; changing its delegate argument to EventArgs would break current public handler contracts.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0046",
        Justification = "This existing XAML control event publishes the TagKey string directly; changing its delegate argument to EventArgs would break current public handler contracts.")]
    public event EventHandler<string>? MenuPointerEntered;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1003",
        Justification = "This existing XAML control event publishes the TagKey string directly; changing its delegate argument to EventArgs would break current public handler contracts.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0046",
        Justification = "This existing XAML control event publishes the TagKey string directly; changing its delegate argument to EventArgs would break current public handler contracts.")]
    public event EventHandler<string>? MenuPointerExited;

    private void OnClick(object sender, RoutedEventArgs e)
        => MenuClick?.Invoke(this, TagKey);

    private void OnPointerOver(object sender, PointerRoutedEventArgs e)
        => MenuPointerEntered?.Invoke(this, TagKey);

    private void OnPointerOut(object sender, PointerRoutedEventArgs e)
        => MenuPointerExited?.Invoke(this, TagKey);

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TerminalMenuButton self)
            self.LabelBlock.Text = (string)e.NewValue;
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TerminalMenuButton self)
            self.DescriptionBlock.Text = $"— {(string)e.NewValue}";
    }
}
