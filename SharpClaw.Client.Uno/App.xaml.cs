using System.Runtime.ExceptionServices;
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
        ClientStartupDiagnostics.Current.Record(ClientStartupStage.AppInitializing);
        UnhandledException += (_, eventArgs) =>
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, eventArgs.Exception);
        try
        {
            this.InitializeComponent();
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.AppInitialized);
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.StartupFailed, exception);
            throw;
        }
    }

    internal Window? MainWindow { get; private set; }
    internal IHost? Host { get; private set; }

    internal static IServiceProvider? Services { get; private set; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "The top-level async-void startup boundary must persist any failure and show a failure window rather than leave an invisible process; failure is not treated as startup success.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100",
        Justification = "Application.OnLaunched is a mandatory void Uno override; the complete awaited launch is caught, journalled and translated into the startup-failure window.")]
    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await LaunchAsync(args).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.StartupFailed, exception);
            // A startup failure must never leave a responsive, invisible process.
            MainWindow ??= new Window();
            MainWindow.Title = "SharpClaw — startup failed";
            MainWindow.Content = new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black),
                Child = new TextBlock
                {
                    Text = $"SharpClaw could not start.\nStartup diagnostics: {ClientStartupDiagnostics.Current.JournalPath}",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24),
                },
            };
            MainWindow.Activate();
        }
    }

    private async Task LaunchAsync(LaunchActivatedEventArgs args)
    {
        ClientStartupDiagnostics.Current.Record(ClientStartupStage.LaunchStarting);
        var frontendInstance = new FrontendInstanceService();
        ClientStartupDiagnostics.Current.Record(ClientStartupStage.InstanceReady);
        var loggingOptions = SharpClawLoggingOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddLocalEnvironment(isDevelopment: false, instancePaths: frontendInstance.Paths)
                .Build());
        _logging = SharpClawLogRuntime.Create(
            "uno",
            frontendInstance.Paths,
            loggingOptions);
        var logging = _logging;
        RegisterGlobalExceptionLogging(logging.SerilogLogger);

        var builder = CreateApplicationBuilder(args, logging, loggingOptions, frontendInstance);
        MainWindow = builder.Window;
        MainWindow.Title = "SharpClaw";
        ClientStartupDiagnostics.Current.Record(ClientStartupStage.BuilderReady);

#if DEBUG
        MainWindow.UseStudio();
#endif
        var navigation = builder.NavigateAsync<Shell>
            (initialNavigate: async (services, navigator) =>
            {
                ClientStartupDiagnostics.Current.Record(ClientStartupStage.InitialNavigationStarting);
                // Capture the service provider early — Host is not yet
                // assigned at this point, but BootPage needs services.
                Services = services;

                // Boot is the module-neutral home, not a transient route into chat.
                // Use the scoped navigator supplied by Uno for this shell, while
                // retaining the same client action boundary as later navigation.
                await new ClientNavigationService(navigator, services.GetRequiredService<ClientActionDispatcher>())
                    .NavigateRouteAsync(this, "Boot", Qualifiers.Nested).ConfigureAwait(true);
            });
        ClientStartupDiagnostics.Current.Record(ClientStartupStage.NavigationScheduled);
        // NavigateAsync installs the Shell synchronously before yielding. The
        // toolkit otherwise defers activation when its native splash is disabled,
        // although host/window initialization may itself require a loaded window.
        // Activate the installed shell BEFORE awaiting host/navigation completion.
        MainWindow.Activate();
        ClientStartupDiagnostics.Current.Record(ClientStartupStage.WindowActivated);
        SetWindowIconFromFile(MainWindow);
        Host = await navigation.ConfigureAwait(true);
        ClientStartupDiagnostics.Current.Record(ClientStartupStage.NavigationReady);

        MainWindow.Closed += OnWindowClosed;
    }

    private Uno.Extensions.Hosting.IApplicationBuilder CreateApplicationBuilder(
        LaunchActivatedEventArgs args,
        SharpClawLogRuntime logging,
        SharpClawLoggingOptions loggingOptions,
        FrontendInstanceService frontendInstance)
    {
        return this.CreateBuilder(args)
            // Add navigation support for toolkit controls such as TabBar and NavigationView
            .UseToolkitNavigation()
            .Configure(host => host
#if DEBUG
                // Switch to Development environment when running in DEBUG
                .UseEnvironment(Environments.Development)
#endif
                .UseLogging(configure: (_, logBuilder) => ConfigureLogging(logBuilder, logging, loggingOptions), enableUnoLogging: true)
                .UseConfiguration(configure: configBuilder =>
                    configBuilder
                        .EmbeddedSource<App>()
                        .Section<AppConfig>()
                )
                // Enable localization (see appsettings.json for supported languages)
                .UseLocalization()
                .ConfigureServices((context, services) => RegisterServices(
                    services, logging, frontendInstance, context.HostingEnvironment.IsDevelopment()))
                .UseNavigation(ReactiveViewModelMappings.ViewModelMappings, RegisterRoutes)
            );
    }

    private static void ConfigureLogging(
        ILoggingBuilder builder,
        SharpClawLogRuntime logging,
        SharpClawLoggingOptions options)
    {
        builder.ClearProviders()
            .AddSerilog(logging.SerilogLogger, dispose: false)
            .SetMinimumLevel(options.MinimumLevel switch
            {
                Serilog.Events.LogEventLevel.Verbose => LogLevel.Trace,
                Serilog.Events.LogEventLevel.Debug => LogLevel.Debug,
                Serilog.Events.LogEventLevel.Information => LogLevel.Information,
                Serilog.Events.LogEventLevel.Warning => LogLevel.Warning,
                Serilog.Events.LogEventLevel.Error => LogLevel.Error,
                _ => LogLevel.Critical,
            })
            .CoreLogLevel(LogLevel.Warning);
    }

    private static void RegisterServices(
        IServiceCollection services,
        SharpClawLogRuntime logging,
        FrontendInstanceService frontendInstance,
        bool isDevelopment)
    {

        services.AddSingleton(logging);
        services.AddSingleton(frontendInstance);
        var isDev = isDevelopment;
        var apiUrl = RegisterProcesses(services, frontendInstance, isDev);
        services.AddSingleton<ClientActionContextSource>();
        services.AddSingleton<ClientActionDispatcher>(sp =>
            ClientActionDispatcher.CreateProduction(
                sp.GetRequiredService<ClientActionContextSource>()));
        services.AddTransient<ClientNavigationService>();
        services.AddSingleton<ModulePackageStore>();
        services.AddSingleton<SharpClawApiClient>(sp =>
            new SharpClawApiClient(
                apiUrl,
                sp.GetRequiredService<ILogger<SharpClawApiClient>>(),
                frontendInstance,
                sp.GetRequiredService<ClientActionDispatcher>()));
        services.AddSingleton<RemoteBackendConnectionService>();
    }

    private static string RegisterProcesses(
        IServiceCollection services, FrontendInstanceService frontendInstance, bool isDev)
    {
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

        return apiUrl;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100",
        Justification = "Uno requires a void Window.Closed handler; all awaited shutdown faults are observed by the journal before the event returns.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "The final UI shutdown boundary observes any action or resource-cleanup fault; exceptions cannot escape its required async-void event contract.")]
    private async void OnWindowClosed(object sender, WindowEventArgs e)
    {
        try
        {
            var services = Host?.Services;
            var actions = services?.GetService<ClientActionDispatcher>();
            if (services is null || actions is null) return;
            await actions.RunCommandAsync("client.app.close",
                async _ => await CloseResourcesAsync(services).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
        }
    }

    private async Task CloseResourcesAsync(IServiceProvider services)
    {
        // Join a committed restart before disposing its process/HTTP owners.
        // Every later cleanup is attempted, retaining the first failure.
        ExceptionDispatchInfo? failure = null;
        failure = await AttemptShutdownStepAsync(
            () => services.GetService<RemoteBackendConnectionService>()?.DisposeAsync() ?? ValueTask.CompletedTask,
            failure).ConfigureAwait(false);
        failure = await AttemptShutdownStepAsync(() =>
        {
            var backend = services.GetService<BackendProcessManager>();
            var gateway = services.GetService<GatewayProcessManager>();
            WindowsStartupManager.RefreshIfNeeded(backend?.ExecutablePath, backend?.ApiUrl,
                gateway?.ExecutablePath, gateway?.GatewayUrl);
            return ValueTask.CompletedTask;
        }, failure).ConfigureAwait(false);
        failure = await AttemptShutdownStepAsync(() =>
        {
            services.GetService<GatewayProcessManager>()?.Dispose();
            return ValueTask.CompletedTask;
        }, failure).ConfigureAwait(false);
        failure = await AttemptShutdownStepAsync(() =>
        {
            services.GetService<BackendProcessManager>()?.Dispose();
            return ValueTask.CompletedTask;
        }, failure).ConfigureAwait(false);
        failure = await AttemptShutdownStepAsync(
            () => services.GetService<SharpClawApiClient>()?.DisposeAsync() ?? ValueTask.CompletedTask,
            failure).ConfigureAwait(false);
        failure = await AttemptShutdownStepAsync(
            () => _logging?.DisposeAsync() ?? ValueTask.CompletedTask, failure).ConfigureAwait(false);
        failure?.Throw();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Each owned shutdown step is attempted even if an earlier step failed. Secondary faults are journalled using bounded metadata and the first fault retains its original stack for the final shutdown boundary.")]
    private static async Task<ExceptionDispatchInfo?> AttemptShutdownStepAsync(
        Func<ValueTask> step, ExceptionDispatchInfo? firstFailure)
    {
        try { await step().ConfigureAwait(false); }
        catch (Exception exception)
        {
            if (firstFailure is null) return ExceptionDispatchInfo.Capture(exception);
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
        }
        return firstFailure;
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
            new ViewMap<MainPage>(),
            new ViewMap<SettingsPage>(),
            new ViewMap<RemoteConnectionPage>()
        );

        routes.Register(
            new RouteMap("", View: views.FindByViewModel<ShellModel>(),
                Nested:
                [
                    new ("Boot", View: views.FindByView<BootPage>(), IsDefault:true),
                    new ("Main", View: views.FindByView<MainPage>()),
                    new ("Settings", View: views.FindByView<SettingsPage>()),
                    new ("RemoteConnection", View: views.FindByView<RemoteConnectionPage>())
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
                SharpClaw.Client.Uno.WindowExtensions.SetWindowIcon(window);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            SharpClaw.Client.Uno.WindowExtensions.SetWindowIcon(window);
        }
    }
}
