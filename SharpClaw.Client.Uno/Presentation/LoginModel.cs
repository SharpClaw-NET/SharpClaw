using SharpClaw.Services;

namespace SharpClaw.Presentation;

public partial record LoginModel(
    IDispatcher Dispatcher,
    ClientNavigationService Navigation,
    IAuthenticationService Authentication,
    ClientActionDispatcher Actions)
{
    public string Title { get; } = "Login";

    public IState<string> Username => State<string>.Value(this, () => string.Empty);

    public IState<string> Password => State<string>.Value(this, () => string.Empty);

    public async ValueTask Login(CancellationToken token)
    {
        var username = await Username ?? string.Empty;
        var password = await Password ?? string.Empty;

        var success = await Actions.RunCommandAsync(
            "auth.login",
            async cancellationToken => await Authentication.LoginAsync(
                Dispatcher,
                new Dictionary<string, string>
                {
                    { nameof(Username), username },
                    { nameof(Password), password },
                }),
            token);
        if (success)
        {
            await Navigation.NavigateViewModelAsync<MainModel>(
                this,
                qualifier: Qualifiers.ClearBackStack);
        }
    }

}
