using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class ClientHostOwnerTests
{
    [TestCase(false, false), TestCase(false, true), TestCase(true, false), TestCase(true, true)]
    public async Task CloseOwnsHostBuiltBeforeInitializationSettlesAsync(bool initializationFails, bool retirementFails)
    {
        var initialFailure = new IOException("Controlled initialization failure.");
        var retireFailure = new InvalidOperationException("Controlled physical retirement failure.");
        var initialization = new ControlledOperationGate(initializationFails ? initialFailure : null);
        var retirement = new ControlledOperationGate(retirementFails ? retireFailure : null);
        var native = new ControlledOperationGate();
        var desktop = new ClientDesktopLifetime();
        var startup = new ClientStartupLifetime();
        var context = new ClosedStartupContext();
        var path = Path.GetTempFileName();
        var lifetime = desktop.RunHostAsync(() => native.RunAsync().AsTask(), () => CloseAsync(startup));
        Task<ClientHostOwner?>? acquisition = null;
        try
        {
            var publications = 0;
            acquisition = context.InvokeAsync(() => startup.AcquireOwnedAsync(async () =>
            {
                var owner = startup.CreateOwned(() => new ClientHostOwner(new LateStartupHost(path, retirement), desktop));
                await initialization.RunAsync().ConfigureAwait(false);
                return owner;
            }, _ => publications++, static owner => owner.DisposeAsync()));
            await initialization.WaitForEntryAsync().ConfigureAwait(false);
            var owner = startup.GetOwned<ClientHostOwner>()
                ?? throw new AssertionException("The built Host must be owned before any initialization result.");
            var close = CloseAsync(startup);
            CloseAsync(startup).Should().BeSameAs(close);
            startup.TryPublish(() => publications++).Should().BeFalse();
            initialization.Release();
            AssertHeldHost(path, close, lifetime);
            native.Release();
            await retirement.WaitForEntryAsync().ConfigureAwait(false);
            AssertHeldHost(path, close, lifetime);
            owner.DisposeAsync().AsTask().Should().BeSameAs(owner.DisposeAsync().AsTask());
            retirement.Release();
            await AssertOutcomeAsync(lifetime, initializationFails ? initialFailure : retirementFails ? retireFailure : null)
                .ConfigureAwait(false);
            publications.Should().Be(0);
            retirement.Calls.Should().Be(1);
            context.Posts.Should().Be(0);
        }
        finally
        {
            await ReleaseAndJoinAsync(lifetime, acquisition, initialization, native, retirement).ConfigureAwait(false);
            AssertReleasedHost(path);
        }
    }

    private static async Task ReleaseAndJoinAsync(Task lifetime, Task? acquisition, params ControlledOperationGate[] gates)
    {
        foreach (var gate in gates) gate.Release();
#pragma warning disable VSTHRD003 // These are the test's retained actual desktop and acquisition tasks. All three physical gates are released before this context-free join, including exceptional test exits.
        await ClientApiReadinessTests.ObserveRetainedReadinessAsync(lifetime).ConfigureAwait(false);
        if (acquisition is not null)
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(acquisition).ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    private static Task CloseAsync(ClientStartupLifetime startup) => startup.CloseAsync(null,
        () => startup.GetOwned<ClientHostOwner>()?.DisposeAsync().AsTask() ?? Task.CompletedTask);

    private static void AssertHeldHost(string path, Task close, Task lifetime)
    {
        close.IsCompleted.Should().BeFalse();
        lifetime.IsCompleted.Should().BeFalse();
        Action open = () => { using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); };
        open.Should().Throw<IOException>();
    }

    private static void AssertReleasedHost(string path)
    {
        try { using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        finally { File.Delete(path); }
    }

    private static async Task AssertOutcomeAsync(Task lifetime, Exception? expected)
    {
#pragma warning disable VSTHRD003 // The test owns this desktop task and releases initialization, native and physical retirement gates before observing the same original outcome.
        if (expected is null) { await lifetime.ConfigureAwait(false); return; }
        Func<Task> wait = () => lifetime;
#pragma warning restore VSTHRD003
        var failure = await wait.Should().ThrowAsync<Exception>().ConfigureAwait(false);
        failure.Which.Should().BeSameAs(expected);
    }
}
