using SharpClaw.Services;

namespace SharpClaw.Presentation;

public class ShellModel
{
    private readonly ClientNavigationService _navigation;

    public ShellModel(
        IAuthenticationService authentication,
        ClientNavigationService navigation)
    {
        _navigation = navigation;
        _authentication = authentication;
        _authentication.LoggedOut += LoggedOut;
    }

    private async void LoggedOut(object? sender, EventArgs e)
    {
        await _navigation.NavigateViewModelAsync<LoginModel>(
            this,
            qualifier: Qualifiers.ClearBackStack);
    }

    private readonly IAuthenticationService _authentication;
}
