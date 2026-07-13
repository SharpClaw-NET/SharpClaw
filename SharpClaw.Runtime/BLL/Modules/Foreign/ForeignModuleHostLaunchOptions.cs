namespace SharpClaw.Runtime.BLL.Modules.Foreign;

internal sealed record class ForeignModuleHostLaunchOptions
{
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

    public required string ExecutablePath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string ModuleDirectory { get; init; }
    public required string ModuleDataDirectory { get; init; }
    public required Uri ControlAddress { get; init; }
    public required string ControlToken { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? HostVersion { get; init; }
    public IServiceProvider? HostServices { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();
    public TimeSpan StartupTimeout { get; init; } = DefaultStartupTimeout;
    public TimeSpan ShutdownTimeout { get; init; } = DefaultShutdownTimeout;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ModuleDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(ModuleDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(ControlToken);

        if (!ControlAddress.IsAbsoluteUri ||
            (ControlAddress.Scheme != Uri.UriSchemeHttp && ControlAddress.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "Foreign module control address must be an absolute HTTP or HTTPS URI.",
                nameof(ControlAddress));
        }

        if (StartupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout), "Startup timeout must be positive.");

        if (ShutdownTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout), "Shutdown timeout must be positive.");
    }
}
