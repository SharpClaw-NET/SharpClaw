using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SharpClaw.Shared.DurableStorage;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using MsLogger = Microsoft.Extensions.Logging.ILogger;

namespace SharpClaw.Shared.Logging;


public sealed class SharpClawLogRuntime : IAsyncDisposable, IDisposable
{
    private readonly bool _ownsStore;
    private readonly DurableSegmentStore _records;
    private readonly Serilog.ILogger _serilogLogger;
    private readonly SharpClawOwnedStoreRetention? _ownedRetention;
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;

    private SharpClawLogRuntime(
        string appName,
        Guid bootId,
        DurableSegmentStore records,
        SharpClawLogDispatcher dispatcher,
        Serilog.ILogger serilogLogger,
        bool ownsStore,
        SharpClawOwnedStoreRetention? ownedRetention)
    {
        AppName = appName;
        BootId = bootId;
        _records = records;
        Dispatcher = dispatcher;
        _serilogLogger = serilogLogger;
        _ownsStore = ownsStore;
        _ownedRetention = ownedRetention;
    }

    public string AppName { get; }
    public Guid BootId { get; }
    public DurableStreamKey ProcessStream => DurableStreamKey.Process(AppName, BootId);
    public SharpClawLogDispatcher Dispatcher { get; }
    public Serilog.ILogger SerilogLogger => _serilogLogger;
    public Exception? RetentionFailure => _ownedRetention?.Failure;
    [SuppressMessage("Design", "RCS1210", Justification = "The published nullable property represents the absence of an owned retention loop; returning a completed task would change that contract.")]
    public Task? RetentionFirstRun => _ownedRetention?.FirstRun;

    public Task<DurableOperationalStreamCatalog> EnumerateOperationalStreamsAsync(
        DurableOperationalStreamEnumerationOptions options,
        CancellationToken cancellationToken = default) =>
        _records.EnumerateOperationalStreamsAsync(options, cancellationToken);

    [SuppressMessage("ApiDesign", "RS0026", Justification = "These existing published overloads and optional defaults are retained for source compatibility; no optional overload is being added.")]
    public static SharpClawLogRuntime Create(
        string appName,
        DurableSegmentStore records,
        SharpClawLoggingOptions options,
        Guid? bootId = null,
        SharpClawOwnedStoreRetentionOptions? retentionOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(options);

        return CreateCore(
            appName,
            records,
            options,
            bootId,
            ownsStore: false,
            retentionOptions: retentionOptions);
    }

    [SuppressMessage("ApiDesign", "RS0026", Justification = "These existing published overloads and optional defaults are retained for source compatibility; no optional overload is being added.")]
    public static SharpClawLogRuntime Create(
        string appName,
        SharpClawInstancePaths paths,
        SharpClawLoggingOptions options,
        Guid? bootId = null,
        SharpClawOwnedStoreRetentionOptions? retentionOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        paths.EnsureDirectories();
        var rootKey = EncryptionKeyResolver.ResolveKey(paths)
            ?? throw new InvalidOperationException("SharpClaw instance encryption key is unavailable.");
        var records = new DurableSegmentStore(new DurableStorageOptions
        {
            RootDirectory = paths.DurableDirectory,
            EncryptionKey = DurableStorageKeyDerivation.Derive(rootKey, "records"),
            AcquireWriterLease = false,
        });
        return CreateCore(
            appName,
            records,
            options,
            bootId,
            ownsStore: true,
            retentionOptions: retentionOptions);
    }

    [SuppressMessage("Usage", "VSTHRD002", Justification = "The published factory is synchronous. Failure cleanup joins only owned background work, whose continuations use ConfigureAwait(false), before rethrowing construction failure.")]
    internal static SharpClawLogRuntime CreateCore(
        string appName,
        DurableSegmentStore records,
        SharpClawLoggingOptions options,
        Guid? bootId,
        bool ownsStore,
        SharpClawOwnedStoreRetentionOptions? retentionOptions)
    {
        var resolvedBootId = bootId ?? Guid.NewGuid();
        SharpClawLogDispatcher? dispatcher = null;
        Serilog.ILogger? logger = null;
        SharpClawOwnedStoreRetention? ownedRetention = null;
        try
        {
            dispatcher = new SharpClawLogDispatcher(records, appName, resolvedBootId, options);
            logger = new LoggerConfiguration()
                .MinimumLevel.Is(options.MinimumLevel)
                .MinimumLevel.Override("Microsoft", options.MicrosoftMinimumLevel)
                .MinimumLevel.Override("Microsoft.AspNetCore", options.AspNetCoreMinimumLevel)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", options.EntityFrameworkCoreMinimumLevel)
                .MinimumLevel.Override("Uno", options.UnoMinimumLevel)
                .Enrich.FromLogContext()
                .WriteTo.Sink(new SharpClawLogSink(dispatcher))
                .CreateLogger();
            ownedRetention = ownsStore ? new SharpClawOwnedStoreRetention(records, retentionOptions) : null;
            return new SharpClawLogRuntime(appName, resolvedBootId, records, dispatcher,
                logger, ownsStore, ownedRetention);
        }
        catch (Exception failure)
        {
            DisposeResourcesAsync(ownedRetention, dispatcher, logger,
                ownsStore ? records : null, failure).ConfigureAwait(false).GetAwaiter().GetResult();
            throw;
        }
    }

    public Task FlushAndSealAsync(CancellationToken cancellationToken = default) =>
        Dispatcher.FlushAndSealAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeResourcesAsync(
                _ownedRetention, Dispatcher, _serilogLogger, _ownsStore ? _records : null));
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "Cleanup attempts every owned resource and then rethrows the first failure, or an aggregate retaining all failures.")]
    private static async Task DisposeResourcesAsync(
        SharpClawOwnedStoreRetention? retention,
        SharpClawLogDispatcher? dispatcher,
        Serilog.ILogger? logger,
        DurableSegmentStore? ownedStore,
        Exception? originalFailure = null)
    {
        var failures = new List<Exception>();
        if (originalFailure is not null)
            failures.Add(originalFailure);
        try
        {
            if (retention is not null)
                await retention.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure) { failures.Add(failure); }
        try
        {
            if (dispatcher is not null)
                await dispatcher.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure) { failures.Add(failure); }
        try
        {
            if (logger is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception failure) { failures.Add(failure); }
        try
        {
            if (ownedStore is not null)
                await ownedStore.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure) { failures.Add(failure); }
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException(failures);
    }

    [SuppressMessage("Usage", "VSTHRD002", Justification = "IDisposable is retained for published synchronous consumers; all asynchronous cleanup uses ConfigureAwait(false) and joins owned background work.")]
    public void Dispose() => DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
}
