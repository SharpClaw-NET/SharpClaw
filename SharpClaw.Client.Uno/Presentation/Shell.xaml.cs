using Microsoft.UI.Xaml.Media;

namespace SharpClaw.Presentation;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010",
    Justification = "This Uno view inherits nongeneric enumeration from the framework for XAML children; it is not a public collection API and adding generic enumeration would change framework semantics.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1724",
    Justification = "Shell is the existing XAML class and Uno navigation shell; its full SharpClaw.Presentation identity is distinct from the unrelated Windows.UI.Shell namespace.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515",
    Justification = "The net10.0 headless target exposes this public Shell XAML type and its framework constructor/binding contracts. The Uno desktop target shares its declaration; changing accessibility would split the existing source/binary contract across these targets.")]
public sealed partial class Shell : UserControl, IContentControlProvider
{
    private readonly DispatcherTimer _dotsTimer;
    private readonly ShellLoadingSource _loading = new();
    private Task? _navigation;
    private int _dotCount;

    public Shell()
    {
        this.InitializeComponent();
        Splash.Source = _loading;

        _dotsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _dotsTimer.Tick += OnDotsTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ContentControl ContentControl => Splash;

    internal void SetLoadingTask(Task navigation) => _navigation = navigation;

    private void OnLoaded(object sender, RoutedEventArgs e) => _dotsTimer.Start();
    private void OnUnloaded(object sender, RoutedEventArgs e) => _dotsTimer.Stop();

    private void OnDotsTick(object? sender, object e)
    {
        if (!IsLoaded) return;
        // Borrow the actual startup task retained by ClientStartupLifetime. This
        // existing UI timer publishes loading completion only while the view is
        // loaded; no asynchronous notification targets a closed dispatcher.
        if (_navigation is { IsCompleted: true })
        {
            _dotsTimer.Stop();
            _loading.Complete();
            return;
        }
        _dotCount = (_dotCount % 3) + 1;
        var dots = new string('.', _dotCount);

        var textBlock = FindChildByName<TextBlock>(Splash, "LoadingDots");
        if (textBlock is not null)
            textBlock.Text = dots;
    }

    private sealed class ShellLoadingSource : Uno.Toolkit.ILoadable
    {
        public bool IsExecuting { get; private set; } = true;
        public event EventHandler? IsExecutingChanged;

        internal void Complete()
        {
            if (!IsExecuting) return;
            IsExecuting = false;
            IsExecutingChanged?.Invoke(this, EventArgs.Empty);
        }
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
