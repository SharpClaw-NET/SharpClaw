using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class LoginPage : Page
{
    private bool _isRegisterMode;
    private bool _isBusy;
    private bool _needsFirstSetup;
    private bool _needsUpgradeSetup;

    private static FontFamily Mono => TerminalUI.Mono;

    private static JsonSerializerOptions JsonOptions => TerminalUI.Json;

    public LoginPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _needsFirstSetup = !FirstSetupMarker.IsCompleted;
        _needsUpgradeSetup = !_needsFirstSetup && FirstSetupMarker.NeedsUpgradeRerun;
        SetupDisclaimer.Visibility = _needsFirstSetup ? Visibility.Visible : Visibility.Collapsed;
        PopulateSavedAccounts();
        UpdateCursor();
    }

    private SharpClawApiClient Api => App.Services!.GetRequiredService<SharpClawApiClient>();

    private void OnCredentialChanged(object sender, RoutedEventArgs e) => UpdateCursor();

    private void UpdateCursor()
    {
        var user = UsernameBox.Text?.Trim() ?? string.Empty;
        var pass = PasswordBox.Password ?? string.Empty;

        var verb = _isRegisterMode ? "sharpclaw register" : "sharpclaw login";
        var parts = new List<string>(3) { verb };
        if (user.Length > 0) parts.Add(user);
        if (pass.Length > 0) parts.Add(new string('*', pass.Length));
        Cursor.SetCommand(string.Join(' ', parts) + " ");
    }

    private async void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var username = UsernameBox.Text?.Trim() ?? string.Empty;
        var password = PasswordBox.Password ?? string.Empty;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            ShowStatus("✗ Username and password are required.", error: true);
            return;
        }

        if (_isRegisterMode)
        {
            var confirm = ConfirmPasswordBox.Password ?? string.Empty;
            if (password != confirm)
            {
                ShowStatus("✗ Passwords do not match.", error: true);
                return;
            }

            await RegisterAsync(username, password);
        }
        else
        {
            await LoginAsync(username, password);
        }
    }

    private async Task LoginAsync(string username, string password)
    {
        _isBusy = true;
        ShowStatus("> Authenticating...", error: false);

        try
        {
            var rememberMe = RememberMeCheck.IsChecked == true;
            var body = JsonSerializer.Serialize(
                new { username, password, rememberMe }, JsonOptions);
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await Api.PostAsync("/auth/login", content);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var login = JsonSerializer.Deserialize<LoginResponseDto>(json, JsonOptions);

                if (login?.AccessToken is not null)
                {
                    Api.SetAccessToken(login.AccessToken);
                    await PersistLoginAsync(username, login, rememberMe);
                    ShowStatus("✓ Authenticated.", error: false, success: true);
                    await Task.Delay(400);

                    var navigator = App.Services!.GetRequiredService<INavigator>();
                    var target = _needsFirstSetup || _needsUpgradeSetup ? "FirstSetup" : "Main";
                    await navigator.NavigateRouteAsync(this, target, qualifier: Qualifiers.ClearBackStack);
                    return;
                }
            }

            ShowStatus("✗ Invalid credentials.", error: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ {ex.Message}", error: true);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task RegisterAsync(string username, string password)
    {
        _isBusy = true;
        ShowStatus("> Creating account...", error: false);

        try
        {
            var body = JsonSerializer.Serialize(
                new { username, password }, JsonOptions);
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await Api.PostAsync("/auth/register", content);

            if (response.IsSuccessStatusCode)
            {
                ShowStatus("✓ Account created. Logging in...", error: false, success: true);
                await Task.Delay(400);
                await LoginAsync(username, password);
                return;
            }

            var error = await response.Content.ReadAsStringAsync();
            ShowStatus($"✗ Registration failed: {error}", error: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ {ex.Message}", error: true);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void OnToggleModeClick(object sender, RoutedEventArgs e)
    {
        _isRegisterMode = !_isRegisterMode;
        ApplyMode();
    }

    // ═══════════════════════════════════════════════════════════════
    // Multi-account persistence & saved accounts UI
    // ═══════════════════════════════════════════════════════════════

    private void PopulateSavedAccounts()
    {
        var store = App.Services?.GetService<AccountStore>();
        if (store is null) { SavedAccountsSection.Visibility = Visibility.Collapsed; return; }

        var accounts = store.GetAccounts();
        SavedAccountsPanel.Children.Clear();

        if (accounts.Count == 0)
        {
            SavedAccountsSection.Visibility = Visibility.Collapsed;
            return;
        }

        SavedAccountsSection.Visibility = Visibility.Visible;

        foreach (var acct in accounts)
        {
            var hasValidRefresh = acct.RememberMe
                && acct.RefreshToken is not null
                && acct.RefreshTokenExpiresAt > DateTimeOffset.UtcNow;

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = TerminalUI.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4),
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            sp.Children.Add(new TextBlock
            {
                Text = hasValidRefresh ? "›" : "·", FontFamily = Mono, FontSize = 12,
                Foreground = TerminalUI.Brush(hasValidRefresh ? 0x32CD32 : 0x808080),
            });
            sp.Children.Add(new TextBlock
            {
                Text = acct.Username, FontFamily = Mono, FontSize = 12,
                Foreground = TerminalUI.Brush(hasValidRefresh ? 0x32CD32 : 0x808080),
            });
            if (hasValidRefresh)
                sp.Children.Add(new TextBlock
                {
                    Text = "→ click to login", FontFamily = Mono, FontSize = 10,
                    Foreground = TerminalUI.Brush(0x808080),
                    VerticalAlignment = VerticalAlignment.Center,
                });

            var captured = acct;
            btn.Click += async (_, _) =>
            {
                if (hasValidRefresh)
                    await AutoLoginWithRefreshAsync(captured);
                else
                {
                    UsernameBox.Text = captured.Username;
                    PasswordBox.Focus(FocusState.Programmatic);
                }
            };
            btn.Content = sp;
            Grid.SetColumn(btn, 0);
            row.Children.Add(btn);

            var removeBtn = TerminalUI.RemoveButton(() =>
            {
                store.RemoveAccount(captured.UserId);
                PopulateSavedAccounts();
            });
            removeBtn.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(removeBtn, 1);
            row.Children.Add(removeBtn);
            SavedAccountsPanel.Children.Add(row);
        }
    }

    private async Task PersistLoginAsync(string username, LoginResponseDto login, bool rememberMe)
    {
        try
        {
            Guid? userId = null;
            using (var meResp = await Api.GetAsync("/auth/me"))
            {
                if (meResp.IsSuccessStatusCode)
                {
                    using var stream = await meResp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);
                    if (doc.RootElement.TryGetProperty("id", out var idProp))
                        userId = idProp.GetGuid();
                }
            }

            if (userId is not { } uid) return;

            var store = App.Services!.GetRequiredService<AccountStore>();
            store.SaveAccount(new AccountStore.SavedAccount
            {
                UserId = uid,
                Username = username,
                AccessToken = login.AccessToken,
                AccessTokenExpiresAt = login.AccessTokenExpiresAt,
                RefreshToken = rememberMe ? login.RefreshToken : null,
                RefreshTokenExpiresAt = rememberMe ? login.RefreshTokenExpiresAt : null,
                RememberMe = rememberMe,
            });

            App.Services!.GetRequiredService<ClientSettings>().SwitchUser(uid);
        }
        catch { /* best-effort — login still succeeds */ }
    }

    private async Task AutoLoginWithRefreshAsync(AccountStore.SavedAccount account)
    {
        if (_isBusy) return;
        _isBusy = true;
        ShowStatus($"> Logging in as {account.Username}...", error: false);

        try
        {
            var body = JsonSerializer.Serialize(
                new { refreshToken = account.RefreshToken }, JsonOptions);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await Api.PostAsync("/auth/refresh", content);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var login = JsonSerializer.Deserialize<LoginResponseDto>(json, JsonOptions);

                if (login?.AccessToken is not null)
                {
                    Api.SetAccessToken(login.AccessToken);

                    var store = App.Services!.GetRequiredService<AccountStore>();
                    store.SaveAccount(new AccountStore.SavedAccount
                    {
                        UserId = account.UserId,
                        Username = account.Username,
                        AccessToken = login.AccessToken,
                        AccessTokenExpiresAt = login.AccessTokenExpiresAt,
                        RefreshToken = login.RefreshToken ?? account.RefreshToken,
                        RefreshTokenExpiresAt = login.RefreshTokenExpiresAt ?? account.RefreshTokenExpiresAt,
                        RememberMe = true,
                    });

                    App.Services!.GetRequiredService<ClientSettings>().SwitchUser(account.UserId);

                    ShowStatus("✓ Authenticated.", error: false, success: true);
                    await Task.Delay(400);

                    var navigator = App.Services!.GetRequiredService<INavigator>();
                    var target = _needsFirstSetup || _needsUpgradeSetup ? "FirstSetup" : "Main";
                    await navigator.NavigateRouteAsync(this, target, qualifier: Qualifiers.ClearBackStack);
                    return;
                }
            }

            // Refresh failed — clear stale tokens
            var s = App.Services?.GetService<AccountStore>();
            if (s is not null)
            {
                account.RefreshToken = null;
                account.RefreshTokenExpiresAt = null;
                account.RememberMe = false;
                s.SaveAccount(account);
            }

            ShowStatus("✗ Session expired. Please log in again.", error: true);
            PopulateSavedAccounts();
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ {ex.Message}", error: true);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void ApplyMode()
    {
        if (_isRegisterMode)
        {
            HeaderBlock.Text = "> Create a new account";
            SubmitLabel.Text = "[ Register ]";
            ToggleModeLabel.Text = "> Already have an account? Login";
            ConfirmLabel.Visibility = Visibility.Visible;
            ConfirmPasswordBox.Visibility = Visibility.Visible;
            SavedAccountsSection.Visibility = Visibility.Collapsed;
        }
        else
        {
            HeaderBlock.Text = "> Authenticate to continue";
            SubmitLabel.Text = "[ Login ]";
            ToggleModeLabel.Text = "> No account? Register";
            ConfirmLabel.Visibility = Visibility.Collapsed;
            ConfirmPasswordBox.Visibility = Visibility.Collapsed;
            PopulateSavedAccounts();
        }

        StatusBlock.Visibility = Visibility.Collapsed;
        UpdateCursor();
    }

    private void ShowStatus(string text, bool error, bool success = false)
    {
        StatusBlock.Text = text;
        StatusBlock.Foreground = TerminalUI.Brush(
            error ? 0xFF4444 : success ? 0x32CD32 : 0x808080);
        StatusBlock.Visibility = Visibility.Visible;
    }

    private sealed record LoginResponseDto(
        string? AccessToken,
        DateTimeOffset? AccessTokenExpiresAt,
        string? RefreshToken,
        DateTimeOffset? RefreshTokenExpiresAt);

    private void OnEnvClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        EnvMenuPage.PendingOrigin = "Login";
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "EnvMenu");
    }
}
