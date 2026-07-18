using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using SharpClaw.Shared.DurableStorage;
using SharpClaw.Shared.Logging;

namespace SharpClaw.Tests.Logging;

[TestFixture]
public sealed class UnifiedLoggingTests
{
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

    private static DurableSegmentStore CreateStore(string root) =>
        new(new DurableStorageOptions
        {
            RootDirectory = root,
            EncryptionKey = Enumerable.Repeat((byte)0x41, 32).ToArray(),
            SegmentMaxBytes = 64 * 1024,
            SegmentMaxAge = TimeSpan.FromHours(1),
        });

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
