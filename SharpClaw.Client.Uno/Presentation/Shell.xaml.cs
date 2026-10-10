using Microsoft.UI.Xaml.Media;

namespace SharpClaw.Presentation;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010",
    Justification = "This Uno view inherits nongeneric enumeration from the framework for XAML children; it is not a public collection API and adding generic enumeration would change framework semantics.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1724",
    Justification = "Shell is the existing XAML class and Uno navigation shell; its full SharpClaw.Presentation identity is distinct from the unrelated Windows.UI.Shell namespace.")]
public sealed partial class Shell : UserControl, IContentControlProvider
{
    private readonly DispatcherTimer _dotsTimer;
    private int _dotCount;

    public Shell()
    {
        this.InitializeComponent();

        _dotsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _dotsTimer.Tick += OnDotsTick;
        _dotsTimer.Start();
    }

    public ContentControl ContentControl => Splash;

    private void OnDotsTick(object? sender, object e)
    {
        _dotCount = (_dotCount % 3) + 1;
        var dots = new string('.', _dotCount);

        var textBlock = FindChildByName<TextBlock>(Splash, "LoadingDots");
        if (textBlock is not null)
            textBlock.Text = dots;
    }

    private static T? FindChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
                return fe;

            var result = FindChildByName<T>(child, name);
            if (result is not null)
                return result;
        }
        return null;
    }
}
