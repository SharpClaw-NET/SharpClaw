using SharpClaw.Services;

namespace SharpClaw.Presentation;

public partial record MainModel
{
    private readonly ClientNavigationService _navigation;
    private readonly ClientActionDispatcher _actions;

    public MainModel(
        IStringLocalizer localizer,
        IOptions<AppConfig> appInfo,
        IAuthenticationService authentication,
        ClientNavigationService navigation,
        ClientActionDispatcher actions)
    {
        _navigation = navigation;
        _actions = actions;
        _authentication = authentication;
        Title = "Main";
        Title += $" - {localizer["ApplicationName"]}";
        Title += $" - {appInfo?.Value?.Environment}";
    }

    public string? Title { get; }

    public IState<string> Name => State<string>.Value(this, () => string.Empty);

    public async Task GoToSecond()
    {
        var name = await Name;
        await _navigation.NavigateViewModelAsync<SecondModel>(
            this,
            data: new Entity(name!));
    }

    public async ValueTask Logout(CancellationToken token)
    {
        await _actions.RunCommandAsync(
            "auth.logout",
            async cancellationToken =>
            {
                await _authentication.LogoutAsync(cancellationToken);
                return true;
            },
            token);
    }

    private IAuthenticationService _authentication;
}
