using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class LoginPage : Page
{
    private bool _isRegisterMode;
    private bool _isBusy;
    private bool _cursorVisible = true;
    private readonly DispatcherTimer _cursorBlinkTimer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LoginPage()
    {
        this.InitializeComponent();

        _cursorBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _cursorBlinkTimer.Tick += (_, _) =>
        {
            _cursorVisible = !_cursorVisible;
            CursorBlock.Text = _cursorVisible ? "_" : " ";
        };
        _cursorBlinkTimer.Start();
    }

    private SharpClawApiClient Api => App.Services!.GetRequiredService<SharpClawApiClient>();

    private void OnCredentialChanged(object sender, RoutedEventArgs e)
    {
        var user = UsernameBox.Text?.Trim() ?? string.Empty;
        var pass = PasswordBox.Password ?? string.Empty;

        var cmd = _isRegisterMode ? "sharpclaw auth register" : "sharpclaw auth login";

        if (user.Length > 0 || pass.Length > 0)
        {
            var maskedPass = pass.Length > 0 ? new string('*', pass.Length) : string.Empty;
            var parts = new List<string>(3) { cmd };
            if (user.Length > 0) parts.Add(user);
            if (maskedPass.Length > 0) parts.Add(maskedPass);
            CursorCommandBlock.Text = string.Join(' ', parts) + " ";
        }
        else
        {
            CursorCommandBlock.Text = string.Empty;
        }
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
            var body = JsonSerializer.Serialize(
                new { username, password, rememberMe = true }, JsonOptions);
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await Api.PostAsync("/auth/login", content);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var login = JsonSerializer.Deserialize<LoginResponseDto>(json, JsonOptions);

                if (login?.AccessToken is not null)
                {
                    Api.SetAccessToken(login.AccessToken);
                    ShowStatus("✓ Authenticated.", error: false, success: true);
                    await Task.Delay(400);

                    var navigator = App.Services!.GetRequiredService<INavigator>();
                    await navigator.NavigateViewModelAsync<MainModel>(this, qualifier: Qualifiers.ClearBackStack);
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

    private void ApplyMode()
    {
        if (_isRegisterMode)
        {
            HeaderBlock.Text = "> Create a new account";
            SubmitLabel.Text = "[ Register ]";
            ToggleModeLabel.Text = "> Already have an account? Login";
            ConfirmLabel.Visibility = Visibility.Visible;
            ConfirmPasswordBox.Visibility = Visibility.Visible;
        }
        else
        {
            HeaderBlock.Text = "> Authenticate to continue";
            SubmitLabel.Text = "[ Login ]";
            ToggleModeLabel.Text = "> No account? Register";
            ConfirmLabel.Visibility = Visibility.Collapsed;
            ConfirmPasswordBox.Visibility = Visibility.Collapsed;
        }

        StatusBlock.Visibility = Visibility.Collapsed;
        CursorCommandBlock.Text = string.Empty;
    }

    private static readonly Windows.UI.Color ColorGray = Windows.UI.Color.FromArgb(255, 128, 128, 128);
    private static readonly Windows.UI.Color ColorGreen = Windows.UI.Color.FromArgb(255, 50, 205, 50);
    private static readonly Windows.UI.Color ColorRed = Windows.UI.Color.FromArgb(255, 255, 68, 68);

    private void ShowStatus(string text, bool error, bool success = false)
    {
        StatusBlock.Text = text;
        StatusBlock.Foreground = new SolidColorBrush(
            error ? ColorRed : success ? ColorGreen : ColorGray);
        StatusBlock.Visibility = Visibility.Visible;
    }

    private sealed record LoginResponseDto(
        string? AccessToken,
        DateTimeOffset? AccessTokenExpiresAt,
        string? RefreshToken,
        DateTimeOffset? RefreshTokenExpiresAt);
}
