using SharpClaw.Configuration;
using SharpClaw.Services;
using SharpClaw.Client.Uno;
using SharpClaw.Shared.Logging;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Uno.Resizetizer;

namespace SharpClaw;

public partial class App : Application
{
    private SharpClawLogRuntime? _logging;

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
        var frontendInstance = new FrontendInstanceService();
        var loggingOptions = SharpClawLoggingOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddLocalEnvironment(isDevelopment: false)
                .Build());
        _logging = SharpClawLogRuntime.Create(
            "uno",
            frontendInstance.Paths,
            loggingOptions);
        var logging = _logging;
        RegisterGlobalExceptionLogging(logging.SerilogLogger);

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
                    logBuilder
                        .ClearProviders()
                        .AddSerilog(logging.SerilogLogger, dispose: false)
                        .SetMinimumLevel(
                            loggingOptions.MinimumLevel switch
                            {
                                Serilog.Events.LogEventLevel.Verbose => LogLevel.Trace,
                                Serilog.Events.LogEventLevel.Debug => LogLevel.Debug,
                                Serilog.Events.LogEventLevel.Information => LogLevel.Information,
                                Serilog.Events.LogEventLevel.Warning => LogLevel.Warning,
                                Serilog.Events.LogEventLevel.Error => LogLevel.Error,
                                _ => LogLevel.Critical,
                            })

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
                    services.AddSingleton(logging);
                    services.AddSingleton(frontendInstance);
                    var isDev = context.HostingEnvironment.IsDevelopment();
                    var configuredApiUrl = LocalEnvironment.LoadApiUrl(isDev);
                    var apiUrl = frontendInstance.ResolvePreferredBackendBaseUrl(configuredApiUrl);
                    if (!string.Equals(configuredApiUrl, LocalEnvironment.DefaultApiUrl, StringComparison.OrdinalIgnoreCase))
                        frontendInstance.RememberBackendBinding(null, configuredApiUrl, "configured");
                    var backendEnabled = LocalEnvironment.LoadBackendEnabled(isDev);
                    var persistent = LocalEnvironment.LoadProcessesPersistent(isDev);

                    services.AddSingleton<BackendProcessManager>(sp =>
                    {
                        var manager = new BackendProcessManager(
                            apiUrl,
                            sp.GetRequiredService<ILogger<BackendProcessManager>>(),
                            frontendInstance)
                        {
                            SkipLaunch = !backendEnabled,
                            Persistent = persistent,
                        };
                        return manager;
                    });

                    var gatewayUrl = LocalEnvironment.LoadGatewayUrl(isDev);
                    var gatewayEnabled = LocalEnvironment.LoadGatewayEnabled(isDev);

                    services.AddSingleton<GatewayProcessManager>(sp =>
                    {
                        var manager = new GatewayProcessManager(
                            gatewayUrl,
                            apiUrl,
                            sp.GetRequiredService<ILogger<GatewayProcessManager>>(),
                            frontendInstance)
                        {
                            SkipLaunch = !gatewayEnabled,
                            Persistent = persistent,
                        };
                        return manager;
                    });

                    services.AddSingleton<ClientActionDispatcher>();
                    services.AddSingleton<ClientNavigationService>();
                    services.AddSingleton<SharpClawApiClient>(sp =>
                        new SharpClawApiClient(
                            apiUrl,
                            sp.GetRequiredService<ILogger<SharpClawApiClient>>(),
                            frontendInstance,
                            sp.GetRequiredService<ClientActionDispatcher>()));
                    services.AddSingleton(sp => new FirstSetupMarker(
                        frontendInstance,
                        sp.GetRequiredService<ClientActionDispatcher>()));
                    services.AddSingleton(sp => new ClientSettings(
                        frontendInstance,
                        sp.GetRequiredService<ClientActionDispatcher>()));
                    services.AddSingleton(sp => new AccountStore(
                        frontendInstance,
                        sp.GetRequiredService<ClientActionDispatcher>()));
                    var moduleStateCache = new ModuleStateCache();
                    var contributionRegistry = new ModuleFrontendContributionRegistry(moduleStateCache);
                    services.AddSingleton(moduleStateCache);
                    services.AddSingleton(contributionRegistry);
                    services.AddSingleton(new ModuleFrontendStateService(moduleStateCache, contributionRegistry));
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
                await services.GetRequiredService<ClientNavigationService>()
                    .NavigateRouteAsync(this, "Boot", Qualifiers.Nested);
            });

        // Dispose managed processes when the app window closes.
        // Persistent mode → Detach (keep running); otherwise → Stop + Kill.
        if (MainWindow is not null)
        {
            MainWindow.Closed += (_, _) =>
            {
                var gw = Host?.Services.GetService<GatewayProcessManager>();
                var be = Host?.Services.GetService<BackendProcessManager>();

                // Refresh auto-start scripts so paths stay current
                // (handles MSIX version-path changes on update).
                WindowsStartupManager.RefreshIfNeeded(
                    be?.ExecutablePath, be?.ApiUrl,
                    gw?.ExecutablePath, gw?.GatewayUrl);

                gw?.Dispose();
                be?.Dispose();
                _logging?.Dispose();
            };
        }
    }

    private static void RegisterGlobalExceptionLogging(Serilog.ILogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                logger.Error(exception, "Unhandled AppDomain exception in Uno.");
            else
                logger.Error(
                    "Unhandled AppDomain exception payload: {ExceptionObject}",
                    eventArgs.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            logger.Error(eventArgs.Exception, "Unobserved task exception in Uno.");
        };
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
            new ViewMap<UserGuidePage>(),
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
                    new ("UserGuide", View: views.FindByView<UserGuidePage>()),
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
