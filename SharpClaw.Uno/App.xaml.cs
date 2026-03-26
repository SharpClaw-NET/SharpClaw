using SharpClaw.Configuration;
using SharpClaw.Services;
using SharpClaw.Uno;
using Uno.Resizetizer;

namespace SharpClaw;

public partial class App : Application
{
    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        this.InitializeComponent();
    }

    internal Window? MainWindow { get; private set; }
    internal IHost? Host { get; private set; }

    internal static IServiceProvider? Services { get; private set; }

    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var builder = this.CreateBuilder(args)
            // Add navigation support for toolkit controls such as TabBar and NavigationView
            .UseToolkitNavigation()
            .Configure(host => host
#if DEBUG
                // Switch to Development environment when running in DEBUG
                .UseEnvironment(Environments.Development)
#endif
                .UseLogging(configure: (context, logBuilder) =>
                {
                    // Configure log levels for different categories of logging
                    logBuilder
                        .SetMinimumLevel(
                            context.HostingEnvironment.IsDevelopment() ?
                                LogLevel.Information :
                                LogLevel.Warning)

                        // Default filters for core Uno Platform namespaces
                        .CoreLogLevel(LogLevel.Warning);

                    // Uno Platform namespace filter groups
                    // Uncomment individual methods to see more detailed logging
                    //// Generic Xaml events
                    //logBuilder.XamlLogLevel(LogLevel.Debug);
                    //// Layout specific messages
                    //logBuilder.XamlLayoutLogLevel(LogLevel.Debug);
                    //// Storage messages
                    //logBuilder.StorageLogLevel(LogLevel.Debug);
                    //// Binding related messages
                    //logBuilder.XamlBindingLogLevel(LogLevel.Debug);
                    //// Binder memory references tracking
                    //logBuilder.BinderMemoryReferenceLogLevel(LogLevel.Debug);
                    //// DevServer and HotReload related
                    //logBuilder.HotReloadCoreLogLevel(LogLevel.Information);
                    //// Debug JS interop
                    //logBuilder.WebAssemblyLogLevel(LogLevel.Debug);

                }, enableUnoLogging: true)
                .UseSerilog(consoleLoggingEnabled: true, fileLoggingEnabled: true)
                .UseConfiguration(configure: configBuilder =>
                    configBuilder
                        .EmbeddedSource<App>()
                        .Section<AppConfig>()
                )
                // Enable localization (see appsettings.json for supported languages)
                .UseLocalization()
                .UseAuthentication(auth =>
    auth.AddCustom(custom =>
            custom
                .Login((sp, dispatcher, credentials, cancellationToken) =>
                {
                    // TODO: Write code to process credentials that are passed into the LoginAsync method
                    if (credentials?.TryGetValue(nameof(LoginModel.Username), out var username) ?? false &&
                        !username.IsNullOrEmpty())
                    {
                        // Return IDictionary containing any tokens used by service calls or in the app
                        credentials ??= new Dictionary<string, string>();
                        credentials[TokenCacheExtensions.AccessTokenKey] = "SampleToken";
                        credentials[TokenCacheExtensions.RefreshTokenKey] = "RefreshToken";
                        credentials["Expiry"] = DateTime.Now.AddMinutes(5).ToString("g");
                        return ValueTask.FromResult<IDictionary<string, string>?>(credentials);
                    }

                    // Return null/default to fail the LoginAsync method
                    return ValueTask.FromResult<IDictionary<string, string>?>(default);
                })
                .Refresh((sp, tokenDictionary, cancellationToken) =>
                {
                    // TODO: Write code to refresh tokens using the currently stored tokens
                    if ((tokenDictionary?.TryGetValue(TokenCacheExtensions.RefreshTokenKey, out var refreshToken) ?? false) &&
                        !refreshToken.IsNullOrEmpty() &&
                        (tokenDictionary?.TryGetValue("Expiry", out var expiry) ?? false) &&
                        DateTime.TryParse(expiry, out var tokenExpiry) &&
                        tokenExpiry > DateTime.Now)
                    {
                        // Return IDictionary containing any tokens used by service calls or in the app
                        tokenDictionary ??= new Dictionary<string, string>();
                        tokenDictionary[TokenCacheExtensions.AccessTokenKey] = "NewSampleToken";
                        tokenDictionary["Expiry"] = DateTime.Now.AddMinutes(5).ToString("g");
                        return ValueTask.FromResult<IDictionary<string, string>?>(tokenDictionary);
                    }

                    // Return null/default to fail the Refresh method
                    return ValueTask.FromResult<IDictionary<string, string>?>(default);
                }), name: "CustomAuth")
                )
                .ConfigureServices((context, services) =>
                {
                    var isDev = context.HostingEnvironment.IsDevelopment();
                    var apiUrl = LocalEnvironment.LoadApiUrl(isDev);
                    var backendEnabled = LocalEnvironment.LoadBackendEnabled(isDev);

                    var backendManager = new BackendProcessManager(apiUrl)
                    {
                        SkipLaunch = !backendEnabled
                    };
                    services.AddSingleton(backendManager);
                    services.AddSingleton(new SharpClawApiClient(apiUrl));
                    services.AddSingleton(new ClientSettings());
                    services.AddSingleton(new AccountStore());
                })
                .UseNavigation(ReactiveViewModelMappings.ViewModelMappings, RegisterRoutes)
            );
        MainWindow = builder.Window;

#if DEBUG
        MainWindow.UseStudio();
#endif
        SetWindowIconFromFile(MainWindow);

        Host = await builder.NavigateAsync<Shell>
            (initialNavigate: async (services, navigator) =>
            {
                // Capture the service provider early — Host is not yet
                // assigned at this point, but BootPage needs services.
                Services = services;

                // Show the terminal-style boot screen which handles
                // connection, retry, and then navigates to Login/Main.
                await navigator.NavigateRouteAsync(this, "Boot", qualifier: Qualifiers.Nested);
            });

        // Stop the backend process when the app window closes.
        if (MainWindow is not null)
        {
            MainWindow.Closed += (_, _) =>
            {
                Host?.Services.GetService<BackendProcessManager>()?.Dispose();
            };
        }
    }

    private static void RegisterRoutes(IViewRegistry views, IRouteRegistry routes)
    {
        views.Register(
            new ViewMap(ViewModel: typeof(ShellModel)),
            new ViewMap<BootPage>(),
            new ViewMap<LoginPage>(),
            new ViewMap<FirstSetupPage>(),
            new ViewMap<MainPage>(),
            new ViewMap<SettingsPage>(),
            new ViewMap<LegalNoticesPage>(),
            new ViewMap<EnvMenuPage>(),
            new ViewMap<EnvEditorPage>()
        );

        routes.Register(
            new RouteMap("", View: views.FindByViewModel<ShellModel>(),
                Nested:
                [
                    new ("Boot", View: views.FindByView<BootPage>()),
                    new ("Login", View: views.FindByView<LoginPage>()),
                    new ("FirstSetup", View: views.FindByView<FirstSetupPage>()),
                    new ("Main", View: views.FindByView<MainPage>(), IsDefault:true),
                    new ("Settings", View: views.FindByView<SettingsPage>()),
                    new ("LegalNotices", View: views.FindByView<LegalNoticesPage>()),
                    new ("EnvMenu", View: views.FindByView<EnvMenuPage>()),
                    new ("EnvEditor", View: views.FindByView<EnvEditorPage>())
                ]
            )
        );
    }

    private static void SetWindowIconFromFile(Window window)
    {
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Environment", "icon.ico");
            if (File.Exists(icoPath))
            {
                var appWindow = window.AppWindow;
                appWindow.SetIcon(icoPath);
            }
            else
            {
                // Fall back to Resizetizer-generated icon
                window.SetWindowIcon();
            }
        }
        catch
        {
            window.SetWindowIcon();
        }
    }
}
