using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
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
}
