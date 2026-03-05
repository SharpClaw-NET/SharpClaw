using Microsoft.UI.Xaml.Media;

namespace SharpClaw.Presentation;

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
            if (child is T fe && fe.Name == name)
                return fe;

            var result = FindChildByName<T>(child, name);
            if (result is not null)
                return result;
        }
        return null;
    }
}
