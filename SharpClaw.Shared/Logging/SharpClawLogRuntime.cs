using System.Collections.Concurrent;
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
    private int _disposed;

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
    public Task? RetentionFirstRun => _ownedRetention?.FirstRun;

    public Task<DurableOperationalStreamCatalog> EnumerateOperationalStreamsAsync(
        DurableOperationalStreamEnumerationOptions options,
        CancellationToken cancellationToken = default) =>
        _records.EnumerateOperationalStreamsAsync(options, cancellationToken);

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

    public static SharpClawLogRuntime Create(
        string appName,
        SharpClawInstancePaths paths,
        SharpClawLoggingOptions options,
        Guid? bootId = null,
        SharpClawOwnedStoreRetentionOptions? retentionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
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

    private static SharpClawLogRuntime CreateCore(
        string appName,
        DurableSegmentStore records,
        SharpClawLoggingOptions options,
        Guid? bootId,
        bool ownsStore,
        SharpClawOwnedStoreRetentionOptions? retentionOptions)
    {
        var resolvedBootId = bootId ?? Guid.NewGuid();
        var dispatcher = new SharpClawLogDispatcher(
            records,
            appName,
            resolvedBootId,
            options);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(options.MinimumLevel)
            .MinimumLevel.Override("Microsoft", options.MicrosoftMinimumLevel)
            .MinimumLevel.Override("Microsoft.AspNetCore", options.AspNetCoreMinimumLevel)
            .MinimumLevel.Override(
                "Microsoft.EntityFrameworkCore",
                options.EntityFrameworkCoreMinimumLevel)
            .MinimumLevel.Override("Uno", options.UnoMinimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.Sink(new SharpClawLogSink(dispatcher))
            .CreateLogger();
        var ownedRetention = ownsStore
            ? new SharpClawOwnedStoreRetention(records, retentionOptions)
            : null;
        return new SharpClawLogRuntime(
            appName,
            resolvedBootId,
            records,
            dispatcher,
            logger,
            ownsStore,
            ownedRetention);
    }

    public Task FlushAndSealAsync(CancellationToken cancellationToken = default) =>
        Dispatcher.FlushAndSealAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_ownedRetention is not null)
            await _ownedRetention.DisposeAsync().ConfigureAwait(false);
        await Dispatcher.DisposeAsync().ConfigureAwait(false);
        if (_serilogLogger is IDisposable disposable)
            disposable.Dispose();
        if (_ownsStore)
            await _records.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
