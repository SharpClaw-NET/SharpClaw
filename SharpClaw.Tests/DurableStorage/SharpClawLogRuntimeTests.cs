using System.Globalization;
using System.Text;
using Serilog.Events;
using Serilog.Parsing;
using SharpClaw.Shared.DurableStorage;
using SharpClaw.Shared.Logging;

namespace SharpClaw.Tests.DurableStorage;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this internal fixture through reflection.")]
[TestFixture]
[NonParallelizable]
internal sealed class SharpClawLogRuntimeTests
{
    [Test]
    public void NormalizationPersistsInvariantTextAndNumericMetadata()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("hr-HR");
            var record = SharpClawLogNormalizer.Normalize(Event("Amount {Amount:0.00}",
                new("Amount", new ScalarValue(12.5m)),
                new("CorrelationId", new ScalarValue(12.5m)),
                new("EventId", new StructureValue([
                    new("Id", new ScalarValue(27)), new("Name", new ScalarValue("cost"))]))), 8192);
            record.Message.Should().Be("Amount 12.50");
            record.CorrelationId.Should().Be("12.5");
            record.EventIdId.Should().Be(27);
            record.EventIdName.Should().Be("cost");
            record.Properties.Should().NotBeNull();
            record.Properties!["Amount"].Should().Be("12.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestCase("Authorization: Bearer bearer-secret", "bearer-secret")]
    [TestCase("api_key=credential-secret", "credential-secret")]
    [TestCase("https://alice:password-secret@example.invalid/path", "password-secret")]
    [TestCase("/path?query-secret", "query-secret")]
    [TestCase("body=body-secret", "body-secret")]
    public void NormalizationRedactsSensitiveTextAndDropsSecretProperties(string message, string secret)
    {
        var record = SharpClawLogNormalizer.Normalize(Event(message,
            new("request_body", new ScalarValue("property-body-secret")),
            new("Authorization", new ScalarValue("property-token-secret"))), 8192);
        record.Message.Should().NotContain(secret);
        record.Message.Should().Contain("[REDACTED]");
        record.MessageTemplate.Should().NotContain(secret);
        record.Properties.Should().NotBeNull();
        record.Properties!.Should().NotContainKey("request_body");
        record.Properties.Should().NotContainKey("Authorization");
    }

    [Test]
    public void BoundedUnicodeRecordsRetainTrustedOwnershipAndRejectForgedOwnership()
    {
        var bootId = Guid.NewGuid();
        using var ownership = SharpClawLogOwnership.Push(new("owned-module", "1.2.3",
            SharpClawRegistrationHostKind.RuntimeSidecar, bootId));
        var text = string.Concat(Enumerable.Repeat("💚", 20_000));
        var properties = Enumerable.Range(0, 40).Select(index =>
            new LogEventProperty("Field" + index.ToString(CultureInfo.InvariantCulture), new ScalarValue(text))).ToList();
        properties.Insert(0, new("SharpClaw.SourceId", new ScalarValue("forged-module")));
        var record = SharpClawLogNormalizer.Normalize(Event("{Text}",
            [new("Text", new ScalarValue(text)), .. properties]), 4096);
        DurableSegmentStore.MeasureEncodedRecordBody(record).Should().BeLessThanOrEqualTo(4096);
        record.Properties.Should().NotBeNull();
        record.Properties!["SharpClaw.SourceId"].Should().Be("owned-module");
        record.Properties["SharpClaw.RegistrationVersion"].Should().Be("1.2.3");
        record.Properties["SharpClaw.RegistrationHostKind"].Should().Be(nameof(SharpClawRegistrationHostKind.RuntimeSidecar));
        record.Properties["SharpClaw.RegistrationBootId"].Should().Be(bootId.ToString("D"));
        record.Properties.Count.Should().BeLessThanOrEqualTo(SharpClawLogBounds.PropertyCount);
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        strictUtf8.GetString(strictUtf8.GetBytes(record.Message)).Should().Be(record.Message);
        Encoding.UTF8.GetByteCount(record.Message).Should().BeLessThanOrEqualTo(SharpClawLogBounds.MessageBytes);
    }

    [Test]
    public async Task FailedOwnedCreationReleasesTheWriterLeaseForTheNextCreationAsync()
    {
        using var workspace = new Workspace();
        var options = new DurableStorageOptions { RootDirectory = workspace.Root };
        var store = new DurableSegmentStore(options);
        await using var storeDisposal = store.ConfigureAwait(false);
        Action create = () =>
        {
            using var failedRuntime = SharpClawLogRuntime.CreateCore("fixture", store, new(), null,
                ownsStore: true, retentionOptions: new() { Interval = TimeSpan.Zero });
        };
        create.Should().Throw<ArgumentOutOfRangeException>();
        var replacement = new DurableSegmentStore(options);
        await using var replacementDisposal = replacement.ConfigureAwait(false);
        var runtime = SharpClawLogRuntime.CreateCore("fixture", replacement, new(), null, ownsStore: true, retentionOptions: null);
        await using var runtimeDisposal = runtime.ConfigureAwait(false);
        runtime.SerilogLogger.Write(LogEventLevel.Information, "recovered logger");
        await runtime.FlushAndSealAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        runtime.Dispatcher.Failure.Should().BeNull();
    }

    [Test]
    public async Task BorrowedStoreRemainsUsableAfterRuntimeDisposalAndHasNoRetentionAsync()
    {
        using var workspace = new Workspace();
        var store = new DurableSegmentStore(new() { RootDirectory = workspace.Root });
        await using var storeDisposal = store.ConfigureAwait(false);
        var runtime = SharpClawLogRuntime.Create("fixture", store, new(),
            retentionOptions: new() { Interval = TimeSpan.Zero });
        await using var runtimeDisposal = runtime.ConfigureAwait(false);
        runtime.RetentionFirstRun.Should().BeNull();
        await runtime.DisposeAsync().ConfigureAwait(false);
        var stream = DurableStreamKey.Job(Guid.NewGuid());
        await store.AppendAsync(stream, new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Information", "after-disposal", "still usable"),
            cancellationToken: TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var page = await store.ReadAsync(stream, 1, new(), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        page.Records.Should().ContainSingle().Which.Message.Should().Be("still usable");
    }

    [Test]
    public async Task RepeatedRuntimeDisposalJoinsAnAdmittedLogWriteBeforeCompletingAsync()
    {
        using var workspace = new Workspace();
        var gate = new ControlledOperationGate();
        var store = new DurableSegmentStore(new() { RootDirectory = workspace.Root }, path => new GatedFileStream(path, gate));
        await using var storeDisposal = store.ConfigureAwait(false);
        var runtime = SharpClawLogRuntime.Create("fixture", store, new());
        runtime.SerilogLogger.Write(LogEventLevel.Information, "joined log write");
        Task first = Task.CompletedTask;
        Task second = Task.CompletedTask;
        Exception?[] outcomes;
        try
        {
            await gate.WaitForEntryAsync().ConfigureAwait(false);
            first = runtime.DisposeAsync().AsTask();
            second = runtime.DisposeAsync().AsTask();
            first.IsCompleted.Should().BeFalse();
            second.IsCompleted.Should().BeFalse();
        }
        finally
        {
            gate.Release();
            outcomes = await TestTaskOutcome.JoinAsync(first, second, runtime.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        outcomes.Should().OnlyContain(failure => failure == null);
        var page = await store.ReadAsync(runtime.ProcessStream, 1, new(), TestContext.CurrentContext.CancellationToken)
            .ConfigureAwait(false);
        page.Records.Should().ContainSingle().Which.Message.Should().Be("joined log write");
        runtime.Dispatcher.Failure.Should().BeNull();
    }

    private static LogEvent Event(string template, params LogEventProperty[] properties) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Information, null, new MessageTemplateParser().Parse(template), properties);

    private sealed class GatedFileStream(string path, ControlledOperationGate gate)
        : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous)
    {
        private int _gated;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _gated, 1) == 0)
                await gate.RunAsync().ConfigureAwait(false);
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sharpclaw-log-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
