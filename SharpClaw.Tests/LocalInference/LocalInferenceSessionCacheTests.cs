using SharpClaw.Application.Core.LocalInference;

namespace SharpClaw.Tests.LocalInference;

/// <summary>
/// Covers item #3 from
/// <c>docs/internal/llamasharp-implementable-gaps.md</c> — cached
/// <see cref="LocalInferenceProcessManager.CachedSession"/> bookkeeping.
/// These tests exercise invalidation, LRU eviction, and disposal using
/// the internal <c>SeedSessionForTest</c> seam; they do not allocate a
/// real <see cref="LLama.LLamaContext"/> (which would require a GGUF
/// file and native runtime).
/// </summary>
[TestFixture]
public class LocalInferenceSessionCacheTests
{
    [Test]
    public void InvalidateSessionRemovesOnlyMatchingKey()
    {
        var mgr = new LocalInferenceProcessManager();
        var modelA = Guid.NewGuid();
        var modelB = Guid.NewGuid();
        var thread1 = Guid.NewGuid();
        var thread2 = Guid.NewGuid();

        mgr.SeedSessionForTest(modelA, thread1);
        mgr.SeedSessionForTest(modelA, thread2);
        mgr.SeedSessionForTest(modelB, thread1);

        mgr.InvalidateSession(modelA, thread1);

        mgr.CachedSessionCount.Should().Be(2);
        mgr.CachedSessionKeys().Should().NotContain((modelA, thread1));
        mgr.CachedSessionKeys().Should().Contain((modelA, thread2));
        mgr.CachedSessionKeys().Should().Contain((modelB, thread1));
    }

    [Test]
    public void InvalidateThreadRemovesEveryModelForThatThread()
    {
        var mgr = new LocalInferenceProcessManager();
        var modelA = Guid.NewGuid();
        var modelB = Guid.NewGuid();
        var thread1 = Guid.NewGuid();
        var thread2 = Guid.NewGuid();

        mgr.SeedSessionForTest(modelA, thread1);
        mgr.SeedSessionForTest(modelB, thread1);
        mgr.SeedSessionForTest(modelA, thread2);

        mgr.InvalidateThread(thread1);

        mgr.CachedSessionCount.Should().Be(1);
        mgr.CachedSessionKeys().Single().Should().Be((modelA, thread2));
    }

    [Test]
    public void LruEvictionDropsOldestWhenCapExceeded()
    {
        var mgr = new LocalInferenceProcessManager { MaxCachedSessions = 2 };
        var model = Guid.NewGuid();
        var oldest = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var newest = Guid.NewGuid();

        var s1 = mgr.SeedSessionForTest(model, oldest);
        s1.LastUsedUtc = DateTime.UtcNow.AddMinutes(-10);
        var s2 = mgr.SeedSessionForTest(model, middle);
        s2.LastUsedUtc = DateTime.UtcNow.AddMinutes(-5);

        // Cap is 2. Seeding a third entry must evict the oldest.
        mgr.SeedSessionForTest(model, newest);

        mgr.CachedSessionCount.Should().Be(2);
        mgr.CachedSessionKeys().Should().NotContain((model, oldest));
        mgr.CachedSessionKeys().Should().Contain((model, middle));
        mgr.CachedSessionKeys().Should().Contain((model, newest));
    }

    [Test]
    public async Task DisposeAsyncDropsAllCachedSessions()
    {
        var mgr = new LocalInferenceProcessManager();
        mgr.SeedSessionForTest(Guid.NewGuid(), Guid.NewGuid());
        mgr.SeedSessionForTest(Guid.NewGuid(), Guid.NewGuid());

        mgr.CachedSessionCount.Should().Be(2);

        await mgr.DisposeAsync();

        mgr.CachedSessionCount.Should().Be(0);
    }

    [Test]
    public void InvalidateSessionIsIdempotent()
    {
        var mgr = new LocalInferenceProcessManager();
        var modelId = Guid.NewGuid();
        var threadId = Guid.NewGuid();

        mgr.SeedSessionForTest(modelId, threadId);
        mgr.InvalidateSession(modelId, threadId);

        // Second call against an already-removed key must not throw.
        Action secondCall = () => mgr.InvalidateSession(modelId, threadId);
        secondCall.Should().NotThrow();
        mgr.CachedSessionCount.Should().Be(0);
    }

    [Test]
    public void InvalidateThreadWithNoMatchingEntriesIsNoop()
    {
        var mgr = new LocalInferenceProcessManager();
        var modelId = Guid.NewGuid();
        mgr.SeedSessionForTest(modelId, Guid.NewGuid());

        mgr.InvalidateThread(Guid.NewGuid());

        mgr.CachedSessionCount.Should().Be(1);
    }
}
