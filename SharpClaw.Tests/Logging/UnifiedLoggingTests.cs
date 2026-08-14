using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using SharpClaw.Runtime.INF.DurableStorage;
using SharpClaw.Shared.DurableStorage;
using SharpClaw.Shared.Logging;

namespace SharpClaw.Tests.Logging;

[TestFixture]
public sealed class UnifiedLoggingTests
{
    [Test]
    public void LegacyConfigurationIsTranslatedWithoutStartupFailure()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:Serilog:Enabled"] = "false",
                ["Logging:Serilog:ConsoleEnabled"] = "true",
                ["Logging:Serilog:RequestLoggingEnabled"] = "false",
                ["Logging:Serilog:MicrosoftMinimumLevel"] = "Error",
                ["Logging:Serilog:FileEnabled"] = "true",
            })
            .Build();

        var options = SharpClawLoggingOptions.FromConfiguration(configuration);

        options.MinimumLevel.Should().Be(Serilog.Events.LogEventLevel.Fatal);
        options.ConsoleEnabled.Should().BeTrue();
        options.RequestLoggingEnabled.Should().BeFalse();
        options.MicrosoftMinimumLevel.Should().Be(Serilog.Events.LogEventLevel.Error);
    }

    [Test]
    public void NewConfigurationTakesPrecedenceOverLegacyValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:MinimumLevel"] = "Debug",
                ["Logging:ConsoleEnabled"] = "false",
                ["Logging:Serilog:Enabled"] = "false",
                ["Logging:Serilog:MinimumLevel"] = "Fatal",
                ["Logging:Serilog:ConsoleEnabled"] = "true",
            })
            .Build();

        var options = SharpClawLoggingOptions.FromConfiguration(configuration);

        options.MinimumLevel.Should().Be(Serilog.Events.LogEventLevel.Debug);
        options.ConsoleEnabled.Should().BeFalse();
    }

    [Test]
    public void MalformedLegacyConfigurationIsIgnored()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:Serilog:Enabled"] = "not-a-boolean",
                ["Logging:Serilog:ConsoleEnabled"] = "not-a-boolean",
                ["Logging:Serilog:MinimumLevel"] = "not-a-level",
            })
            .Build();

        var options = SharpClawLoggingOptions.FromConfiguration(configuration);

        options.MinimumLevel.Should().Be(Serilog.Events.LogEventLevel.Information);
        options.ConsoleEnabled.Should().BeFalse();
    }

    [Test]
    public async Task HostileMetadataIsEncodedWithinTheStoreLimitAndTrustedPropertiesSurvive()
    {
        var root = CreateRoot();
        try
        {
            await using var store = CreateStore(root, maxRecordBytes: 4 * 1024);
            var bootId = Guid.NewGuid();
            await using var runtime = SharpClawLogRuntime.Create(
                "core",
                store,
                new SharpClawLoggingOptions { ConsoleEnabled = false },
                bootId);
            using var provider = new SerilogLoggerProvider(runtime.SerilogLogger, dispose: false);
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            using var moduleFactory = new SharpClawModuleLoggerFactory(
                factory,
                new SharpClawModuleLogContext(
                    "module-a",
                    "1.2.3",
                    SharpClawModuleHostKind.RuntimeInProcess,
                    bootId));

            var properties = Enumerable.Range(0, 28)
                .ToDictionary(
                    index => $"Property-{index:D2}\u0001",
                    _ => (object?)new string('p', 12_000));
            properties["SharpClaw.ModuleId"] = "forged-module";
            properties["SharpClaw.ModuleVersion"] = "forged-version";
            properties["SharpClaw.ModuleHostKind"] = "forged-host";
            properties["SharpClaw.ModuleBootId"] = Guid.Empty.ToString("D");

            var hostileLogger = factory.CreateLogger(new string('c', 20_000) + "\u0002");
            hostileLogger.LogError(
                new EventId(17, new string('e', 20_000) + "\u0003"),
                new InvalidOperationException(new string('x', 200_000) + "\u0004"),
                "hostile {Payload}",
                new string('m', 200_000) + "\u0005");
            moduleFactory.CreateLogger<UnifiedLoggingTests>().Log(
                LogLevel.Error,
                new EventId(18, "module-event"),
                properties,
                null,
                static (_, _) => "module ownership");

            await runtime.FlushAndSealAsync();

            var process = await store.ReadAsync(
                DurableStreamKey.Process("core", bootId),
                1,
                new DurableReadOptions());
            var processRecord = process.Records.Should().ContainSingle().Subject;
            Encoding.UTF8.GetByteCount(processRecord.Category!).Should().BeLessThanOrEqualTo(
                SharpClawLogBounds.CategoryBytes);
            Encoding.UTF8.GetByteCount(processRecord.EventName).Should().BeLessThanOrEqualTo(
                SharpClawLogBounds.EventNameBytes);
            Encoding.UTF8.GetByteCount(processRecord.EventIdName!).Should().BeLessThanOrEqualTo(
                SharpClawLogBounds.EventIdNameBytes);
            Encoding.UTF8.GetByteCount(processRecord.ExceptionText!).Should().BeLessThanOrEqualTo(
                SharpClawLogBounds.ExceptionBytes);
            var processDirectory = new DurableStreamPathEncoder(root)
                .GetStreamDirectory(DurableStreamKey.Process("core", bootId));
            ReadBodyLength(Directory.GetFiles(processDirectory, "*.scseg")
                .Should().ContainSingle().Subject).Should().BeLessThanOrEqualTo(4 * 1024);

            var module = await store.ReadAsync(
                DurableStreamKey.Module("module-a", bootId),
                1,
                new DurableReadOptions());
            var record = module.Records.Should().ContainSingle().Subject;
            record.Properties.Should().ContainKey("SharpClaw.ModuleId");
            record.Properties!["SharpClaw.ModuleId"].Should().Be("module-a");
            record.Properties["SharpClaw.ModuleVersion"].Should().Be("1.2.3");
            record.Properties["SharpClaw.ModuleHostKind"]
                .Should().Be(nameof(SharpClawModuleHostKind.RuntimeInProcess));
            record.Properties["SharpClaw.ModuleBootId"].Should().Be(bootId.ToString("D"));

            var streamDirectory = new DurableStreamPathEncoder(root)
                .GetStreamDirectory(DurableStreamKey.Module("module-a", bootId));
            var segment = Directory.GetFiles(streamDirectory, "*.scseg")
                .Should().ContainSingle().Subject;
            ReadBodyLength(segment).Should().BeLessThanOrEqualTo(4 * 1024);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Test]
    public async Task OwnedStoreRetentionRunsOnceAndAwaitsCancellation()
    {
        var root = CreateRoot();
        try
        {
            await using var store = CreateStore(root);
            var key = DurableStreamKey.Process("gateway", Guid.NewGuid());
            await store.AppendAsync(key, new DurableRecordWrite(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "Information",
                "retention.test",
                "old"));
            await store.SealAsync(key);
            var segment = Directory.GetFiles(
                    root,
                    "*.scseg",
                    SearchOption.AllDirectories)
                .Should().ContainSingle().Subject;
            File.SetLastWriteTimeUtc(segment, DateTime.UtcNow.AddDays(-30));

            var retention = new SharpClawOwnedStoreRetention(
                store,
                new SharpClawOwnedStoreRetentionOptions
                {
                    Interval = TimeSpan.FromDays(1),
                    Retention = new DurableRetentionOptions
                    {
                        ProcessLogAge = TimeSpan.FromDays(1),
                        ModuleLogAge = TimeSpan.FromDays(1),
                        MaximumEncodedBytes = long.MaxValue,
                        MinimumFreeBytes = 0,
                        MaximumDeletesPerRun = 1,
                    },
                });

            await retention.FirstRun.WaitAsync(TimeSpan.FromSeconds(5));
            retention.Failure.Should().BeNull();
            Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
                .Should().BeEmpty();

            await retention.DisposeAsync();
            retention.Completion.IsCompleted.Should().BeTrue();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Test]
    public async Task ShutdownPersistsTerminalDropSummaryBeforeSealing()
    {
        var root = CreateRoot();
        var originalOutput = Console.Out;
        var blockingOutput = new BlockingTextWriter();
        try
        {
            await using var store = CreateStore(root);
            var bootId = Guid.NewGuid();
            await using var runtime = SharpClawLogRuntime.Create(
                "core",
                store,
                new SharpClawLoggingOptions
                {
                    ConsoleEnabled = true,
                    QueueCapacity = 1,
                    FlushInterval = TimeSpan.FromMinutes(1),
                },
                bootId);
            using var provider = new SerilogLoggerProvider(runtime.SerilogLogger, dispose: false);
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            using var moduleFactory = new SharpClawModuleLoggerFactory(
                factory,
                new SharpClawModuleLogContext(
                    "module-a",
                    "1.2.3",
                    SharpClawModuleHostKind.RuntimeInProcess,
                    bootId));

            Console.SetOut(blockingOutput);
            factory.CreateLogger<UnifiedLoggingTests>().LogInformation("first");
            blockingOutput.Started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            factory.CreateLogger<UnifiedLoggingTests>().LogInformation("queued");
            moduleFactory.CreateLogger<UnifiedLoggingTests>().LogInformation("terminal drop");
            runtime.Dispatcher.DroppedRecords.Should().BeGreaterThan(0);

            var shutdown = runtime.FlushAndSealAsync().AsTask();
            blockingOutput.Release.Set();
            await shutdown;

            var process = await store.ReadAsync(
                DurableStreamKey.Process("core", bootId),
                1,
                new DurableReadOptions(MaxScanBytes: 1024 * 1024));
            var module = await store.ReadAsync(
                DurableStreamKey.Module("module-a", bootId),
                1,
                new DurableReadOptions(MaxScanBytes: 1024 * 1024));
            process.Records.Should().NotContain(record => record.EventName == "RecordsDropped");
            module.Records.Should().ContainSingle(record =>
                record.EventName == "RecordsDropped" &&
                record.Properties!["DroppedCount"] == "1");
        }
        finally
        {
            blockingOutput.Release.Set();
            Console.SetOut(originalOutput);
            DeleteRoot(root);
        }
    }

    [Test]
    public async Task ShutdownDrainsAcceptedRecordsSealsKnownStreamsAndIsIdempotent()
    {
        var root = CreateRoot();
        try
        {
            await using var store = CreateStore(root);
            var bootId = Guid.NewGuid();
            await using var runtime = SharpClawLogRuntime.Create(
                "core",
                store,
                new SharpClawLoggingOptions
                {
                    ConsoleEnabled = false,
                    QueueCapacity = 128,
                    FlushInterval = TimeSpan.FromMilliseconds(10),
                },
                bootId);
            using var provider = new SerilogLoggerProvider(runtime.SerilogLogger, dispose: false);
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            using var moduleFactory = new SharpClawModuleLoggerFactory(
                factory,
                new SharpClawModuleLogContext(
                    "module-a",
                    "1.2.3",
                    SharpClawModuleHostKind.RuntimeInProcess,
                    bootId));

            var processLogger = factory.CreateLogger<UnifiedLoggingTests>();
            for (var index = 0; index < 20; index++)
                processLogger.LogInformation("process-{Index}", index);
            moduleFactory.CreateLogger<UnifiedLoggingTests>()
                .LogWarning("module-terminal");

            await runtime.FlushAndSealAsync();
            await runtime.FlushAndSealAsync();

            var process = await store.ReadAsync(
                DurableStreamKey.Process("core", bootId),
                1,
                new DurableReadOptions(MaxScanBytes: 1024 * 1024));
            var module = await store.ReadAsync(
                DurableStreamKey.Module("module-a", bootId),
                1,
                new DurableReadOptions(MaxScanBytes: 1024 * 1024));
            process.Records.Should().HaveCount(20);
            module.Records.Should().ContainSingle(record =>
                record.Message.Contains("module-terminal", StringComparison.Ordinal));
            Directory.GetFiles(root, "*.open", SearchOption.AllDirectories)
                .Should().BeEmpty();
            Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
                .Should().HaveCount(2);
            runtime.Dispatcher.Failure.Should().BeNull();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OneLoggerEventUsesExactlyOneBootStreamRegardlessOfConsole(bool consoleEnabled)
    {
        var root = CreateRoot();
        try
        {
            await using var store = CreateStore(root);
            var bootId = Guid.NewGuid();
            await using var runtime = SharpClawLogRuntime.Create(
                "core",
                store,
                new SharpClawLoggingOptions { ConsoleEnabled = consoleEnabled },
                bootId);
            using var provider = new SerilogLoggerProvider(runtime.SerilogLogger, dispose: false);
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));

            factory.CreateLogger<UnifiedLoggingTests>().LogInformation(
                new EventId(17, "OneEvent"),
                "one event {Value}",
                "value");
            await runtime.FlushAndSealAsync();

            var process = await store.ReadAsync(
                DurableStreamKey.Process("core", bootId),
                1,
                new DurableReadOptions());
            process.Records.Should().ContainSingle(record => record.Message.Contains("one event"));
            process.Records.Single().Category.Should().Contain(nameof(UnifiedLoggingTests));
            process.Records.Single().EventIdId.Should().Be(17);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Test]
    public async Task ExceptionIsOneRecordAndSecretsAreRedacted()
    {
        var root = CreateRoot();
        try
        {
            await using var store = CreateStore(root);
            var bootId = Guid.NewGuid();
            await using var runtime = SharpClawLogRuntime.Create(
                "core",
                store,
                new SharpClawLoggingOptions { ConsoleEnabled = false },
                bootId);
            using var provider = new SerilogLoggerProvider(runtime.SerilogLogger, dispose: false);
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));

            factory.CreateLogger<UnifiedLoggingTests>().LogError(
                new InvalidOperationException("authorization: Bearer top-secret"),
                "request failed authorization={Authorization}",
                "Bearer top-secret");
            await runtime.FlushAndSealAsync();

            var process = await store.ReadAsync(
                DurableStreamKey.Process("core", bootId),
                1,
                new DurableReadOptions());
            process.Records.Should().ContainSingle();
            var record = process.Records.Single();
            record.ExceptionText.Should().NotContain("top-secret");
            record.Message.Should().NotContain("top-secret");
            record.ExceptionText.Should().Contain("[REDACTED]");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Test]
    public async Task HttpDiagnosticsAndRenderedOutputExcludeBodiesCredentialsAndUriQueries()
    {
        var root = CreateRoot();
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            await using var store = CreateStore(root);
            var bootId = Guid.NewGuid();
            await using var runtime = SharpClawLogRuntime.Create(
                "core",
                store,
                new SharpClawLoggingOptions
                {
                    MinimumLevel = Serilog.Events.LogEventLevel.Debug,
                    ConsoleEnabled = true,
                },
                bootId);
            using var provider = new SerilogLoggerProvider(runtime.SerilogLogger, dispose: false);
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));

            Console.SetOut(output);
            Console.SetError(error);
            var logger = factory.CreateLogger<HttpLoggingDelegatingHandler>();
            const string uri =
                "https://user:uri-password@example.test/private?token=query-secret";
            using var client = new HttpClient(
                new HttpLoggingDelegatingHandler(
                    logger,
                    new FixedHttpMessageHandler()));
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent("request-body-secret"),
            };
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                "Bearer header-secret");
            request.Headers.TryAddWithoutValidation("Cookie", "session=cookie-secret");
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

            factory.CreateLogger<UnifiedLoggingTests>().LogWarning(
                new InvalidOperationException(
                    "failed uri=https://user:uri-password@example.test/private?token=query-secret password=exception-secret"),
                "request uri={Uri} headers={Headers} body={Body} prompt={Prompt}",
                uri,
                "Authorization: Bearer header-secret; Cookie=cookie-secret",
                "request-body-secret",
                "prompt-secret");

            await runtime.FlushAndSealAsync();

            var page = await store.ReadAsync(
                DurableStreamKey.Process("core", bootId),
                1,
                new DurableReadOptions(MaxScanBytes: 1024 * 1024));
            page.Records.Should().NotBeEmpty();
            var durableText = string.Join(
                Environment.NewLine,
                page.Records.SelectMany(record =>
                    new[]
                    {
                        record.Message,
                        record.ExceptionText ?? string.Empty,
                        string.Join(
                            Environment.NewLine,
                            record.Properties?.Values
                                ?? Enumerable.Empty<string>()),
                    }));
            durableText.Should().NotContain("uri-password");
            durableText.Should().NotContain("query-secret");
            durableText.Should().NotContain("header-secret");
            durableText.Should().NotContain("cookie-secret");
            durableText.Should().NotContain("request-body-secret");
            durableText.Should().NotContain("response-body-secret");
            durableText.Should().NotContain("exception-secret");
            durableText.Should().NotContain("prompt-secret");
            durableText.Should().Contain("[REDACTED]");

            var consoleText = output + Environment.NewLine + error;
            consoleText.Should().NotContain("uri-password");
            consoleText.Should().NotContain("query-secret");
            consoleText.Should().NotContain("header-secret");
            consoleText.Should().NotContain("cookie-secret");
            consoleText.Should().NotContain("request-body-secret");
            consoleText.Should().NotContain("response-body-secret");
            consoleText.Should().NotContain("exception-secret");
            consoleText.Should().NotContain("prompt-secret");
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            DeleteRoot(root);
        }
    }

    [Test]
    public void BoundedTextTailRetainsOnlyTheConfiguredUtf8Tail()
    {
        var tail = new SharpClawBoundedTextTail(128);
        tail.AppendLine(new string('a', 200));
        tail.AppendLine("latest-line");

        tail.EncodedBytes.Should().BeLessThanOrEqualTo(128);
        tail.Snapshot().Should().ContainSingle().Which.Should().Be("latest-line");

        tail.Clear();
        tail.Count.Should().Be(0);
        tail.EncodedBytes.Should().Be(0);
    }

    [Test]
    public async Task ModuleLoggerUsesTrustedModuleStreamAndCannotBeReroutedByProperties()
    {
        var root = CreateRoot();
        try
        {
            await using var store = CreateStore(root);
            var bootId = Guid.NewGuid();
            var moduleBootId = bootId;
            await using var runtime = SharpClawLogRuntime.Create(
                "core",
                store,
                new SharpClawLoggingOptions { ConsoleEnabled = false },
                bootId);
            using var provider = new SerilogLoggerProvider(runtime.SerilogLogger, dispose: false);
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            using var moduleFactory = new SharpClawModuleLoggerFactory(
                factory,
                new SharpClawModuleLogContext(
                    "module-a",
                    "1.2.3",
                    SharpClawModuleHostKind.RuntimeInProcess,
                    moduleBootId));

            moduleFactory.CreateLogger<UnifiedLoggingTests>().LogInformation(
                "module event {SharpClaw.ModuleId}",
                "forged-process");
            await runtime.FlushAndSealAsync();

            var module = await store.ReadAsync(
                DurableStreamKey.Module("module-a", moduleBootId),
                1,
                new DurableReadOptions());
            var process = await store.ReadAsync(
                DurableStreamKey.Process("core", bootId),
                1,
                new DurableReadOptions());
            module.Records.Should().ContainSingle();
            process.Records.Should().BeEmpty();
            module.Records.Single().Properties!["SharpClaw.ModuleId"]
                .Should().Be("module-a");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static DurableSegmentStore CreateStore(
        string root,
        int maxRecordBytes = 16 * 1024) =>
        new(new DurableStorageOptions
        {
            RootDirectory = root,
            EncryptionKey = Enumerable.Repeat((byte)0x41, 32).ToArray(),
            SegmentMaxBytes = 64 * 1024,
            SegmentMaxAge = TimeSpan.FromHours(1),
            MaxRecordBytes = maxRecordBytes,
        });

    private static int ReadBodyLength(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        stream.Position = 40;
        using var reader = new BinaryReader(stream);
        var frameLength = reader.ReadInt32();
        frameLength.Should().BeGreaterThan(0);
        var frame = reader.ReadBytes(frameLength);
        frame.Should().HaveCount(frameLength);
        return BitConverter.ToInt32(frame, sizeof(long) + 16 + sizeof(long) + sizeof(byte));
    }

    private static string CreateRoot() => Path.Combine(
        Path.GetTempPath(),
        "SharpClawUnifiedLoggingTests_" + Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class FixedHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("response-body-secret"),
            });
    }

    private sealed class BlockingTextWriter : TextWriter
    {
        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char[] buffer, int index, int count)
        {
            Started.Set();
            Release.Wait();
        }

        public override void WriteLine(string? value)
        {
            Started.Set();
            Release.Wait();
        }
    }
}
