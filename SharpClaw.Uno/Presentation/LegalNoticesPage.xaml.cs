using SharpClaw.Helpers;

namespace SharpClaw.Presentation;

public sealed partial class LegalNoticesPage : Page
{
    public LegalNoticesPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/PRIVACY_POLICY.txt"));
            PolicyText.Text = await Windows.Storage.FileIO.ReadTextAsync(file);
        }
        catch
        {
            PolicyText.Text = "Unable to load privacy policy.";
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "Main");
    }
}
