using Microsoft.UI.Xaml.Input;

namespace SharpClaw.Presentation;

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

    public event EventHandler<string>? MenuClick;
    public event EventHandler<string>? MenuPointerEntered;
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
