using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Jobs;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class HostAgentJobControllerTests
{
    [Test]
    public async Task AddJobLogAsync_WhenJobExists_SavesAndAppendsCachedLog()
    {
        var cache = CreateCache();
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var controller = CreateController(db, cache);
        var job = Job(AgentJobStatus.Executing);
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();
        saveProbe.Reset();
        cache.SetJobLogs(
            job.Id,
            [new AgentJobLogResponse("existing", JobLogLevels.Info, DateTimeOffset.UnixEpoch)]);

        await controller.AddJobLogAsync(job.Id, "arbitrary", JobLogLevels.Warning);

        saveProbe.SaveCount.Should().Be(1);
        var saved = await db.AgentJobs
            .Include(j => j.LogEntries)
            .SingleAsync(j => j.Id == job.Id);
        saved.LogEntries.Should().ContainSingle(log =>
            log.Message == "arbitrary"
            && log.Level == JobLogLevels.Warning);
        cache.TryGetJobLogs(job.Id, out var cached).Should().BeTrue();
        cached!.Select(log => log.Message).Should().Equal("existing", "arbitrary");
    }

    [Test]
    public async Task AddJobLogAsync_WhenJobIsMissing_DoesNotSaveOrCache()
    {
        var cache = CreateCache();
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var controller = CreateController(db, cache);
        var missingId = Guid.NewGuid();

        await controller.AddJobLogAsync(missingId, "missing");

        saveProbe.SaveCount.Should().Be(0);
        cache.TryGetJobLogs(missingId, out _).Should().BeFalse();
    }

    [Test]
    public async Task MarkJobCompletedAsync_AppliesCoreDecisionSavesAndAppendsCacheAfterSave()
    {
        var cache = CreateCache();
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var controller = CreateController(db, cache);
        var job = Job(AgentJobStatus.Executing, resultData: "previous");
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();
        saveProbe.Reset();
        cache.SetJobLogs(
            job.Id,
            [new AgentJobLogResponse("existing", JobLogLevels.Info, DateTimeOffset.UnixEpoch)]);
        saveProbe.OnSaving = () =>
        {
            cache.TryGetJobLogs(job.Id, out var duringSave).Should().BeTrue();
            duringSave!.Select(log => log.Message).Should().Equal("existing");
        };

        await controller.MarkJobCompletedAsync(job.Id, resultData: null);

        saveProbe.SaveCount.Should().Be(1);
        var saved = await db.AgentJobs
            .Include(j => j.LogEntries)
            .SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(AgentJobStatus.Completed);
        saved.CompletedAt.Should().NotBeNull();
        saved.ResultData.Should().Be("previous");
        saved.LogEntries.Should().ContainSingle(log =>
            log.Message == "Job completed by module."
            && log.Level == JobLogLevels.Info);
        cache.TryGetJobLogs(job.Id, out var cached).Should().BeTrue();
        cached!.Select(log => log.Message).Should()
            .Equal("existing", "Job completed by module.");
    }

    [Test]
    public async Task MarkJobFailedAsync_AppliesCoreDecisionSavesAndAppendsCacheAfterSave()
    {
        var cache = CreateCache();
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var controller = CreateController(db, cache);
        var job = Job(AgentJobStatus.Executing);
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();
        saveProbe.Reset();
        cache.SetJobLogs(
            job.Id,
            [new AgentJobLogResponse("existing", JobLogLevels.Info, DateTimeOffset.UnixEpoch)]);
        saveProbe.OnSaving = () =>
        {
            cache.TryGetJobLogs(job.Id, out var duringSave).Should().BeTrue();
            duringSave!.Select(log => log.Message).Should().Equal("existing");
        };
        var exception = new InvalidOperationException("late failure");

        await controller.MarkJobFailedAsync(job.Id, exception);

        saveProbe.SaveCount.Should().Be(1);
        var saved = await db.AgentJobs
            .Include(j => j.LogEntries)
            .SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(AgentJobStatus.Failed);
        saved.CompletedAt.Should().NotBeNull();
        saved.ErrorLog.Should().Be(exception.ToString());
        saved.LogEntries.Should().ContainSingle(log =>
            log.Message == "Job failed: late failure"
            && log.Level == JobLogLevels.Error);
        cache.TryGetJobLogs(job.Id, out var cached).Should().BeTrue();
        cached!.Select(log => log.Message).Should()
            .Equal("existing", "Job failed: late failure");
    }

    [Test]
    public async Task MarkJobFailedAsync_WhenJobIsTerminal_DoesNotMutateSaveOrCache()
    {
        var cache = CreateCache();
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var controller = CreateController(db, cache);
        var job = Job(AgentJobStatus.Completed, resultData: "done");
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();
        saveProbe.Reset();
        cache.SetJobLogs(
            job.Id,
            [new AgentJobLogResponse("existing", JobLogLevels.Info, DateTimeOffset.UnixEpoch)]);

        await controller.MarkJobFailedAsync(
            job.Id,
            new InvalidOperationException("late failure"));

        saveProbe.SaveCount.Should().Be(0);
        var saved = await db.AgentJobs
            .Include(j => j.LogEntries)
            .SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(AgentJobStatus.Completed);
        saved.ErrorLog.Should().BeNull();
        saved.LogEntries.Should().BeEmpty();
        cache.TryGetJobLogs(job.Id, out var cached).Should().BeTrue();
        cached!.Select(log => log.Message).Should().Equal("existing");
    }

    [Test]
    public async Task MarkJobFailedAsync_WhenJobIsMissing_DoesNotSave()
    {
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var controller = CreateController(db, CreateCache());

        await controller.MarkJobFailedAsync(
            Guid.NewGuid(),
            new InvalidOperationException("missing"));

        saveProbe.SaveCount.Should().Be(0);
    }

    [Test]
    public async Task MarkJobCancelledAsync_UsesDefaultMessageSavesAndAppendsCacheAfterSave()
    {
        var cache = CreateCache();
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var controller = CreateController(db, cache);
        var job = Job(AgentJobStatus.Executing);
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();
        saveProbe.Reset();
        cache.SetJobLogs(
            job.Id,
            [new AgentJobLogResponse("existing", JobLogLevels.Info, DateTimeOffset.UnixEpoch)]);
        saveProbe.OnSaving = () =>
        {
            cache.TryGetJobLogs(job.Id, out var duringSave).Should().BeTrue();
            duringSave!.Select(log => log.Message).Should().Equal("existing");
        };

        await controller.MarkJobCancelledAsync(job.Id);

        saveProbe.SaveCount.Should().Be(1);
        var saved = await db.AgentJobs
            .Include(j => j.LogEntries)
            .SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(AgentJobStatus.Cancelled);
        saved.CompletedAt.Should().NotBeNull();
        saved.LogEntries.Should().ContainSingle(log =>
            log.Message == "Job cancelled by module."
            && log.Level == JobLogLevels.Warning);
        cache.TryGetJobLogs(job.Id, out var cached).Should().BeTrue();
        cached!.Select(log => log.Message).Should()
            .Equal("existing", "Job cancelled by module.");
    }

    [Test]
    public async Task MarkJobCancelledAsync_UsesCustomMessage()
    {
        var cache = CreateCache();
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var controller = CreateController(db, cache);
        var job = Job(AgentJobStatus.Queued);
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();
        saveProbe.Reset();

        await controller.MarkJobCancelledAsync(job.Id, "custom cancel");

        saveProbe.SaveCount.Should().Be(1);
        var saved = await db.AgentJobs
            .Include(j => j.LogEntries)
            .SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(AgentJobStatus.Cancelled);
        saved.LogEntries.Should().ContainSingle(log =>
            log.Message == "custom cancel"
            && log.Level == JobLogLevels.Warning);
    }

    [Test]
    public async Task CancelStaleJobsByActionPrefixAsync_CancelsOnlyQueuedExecutingMatchesOnce()
    {
        var cache = CreateCache();
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var controller = CreateController(db, cache);
        var queuedMatch = Job(AgentJobStatus.Queued, actionKey: "Curativa.Audio.Start");
        var executingMatch = Job(AgentJobStatus.Executing, actionKey: "curativa.audio.stop");
        var pausedMatch = Job(AgentJobStatus.Paused, actionKey: "curativa.audio.pause");
        var nullAction = Job(AgentJobStatus.Queued, actionKey: null);
        var queuedOther = Job(AgentJobStatus.Queued, actionKey: "other.audio.start");
        var completedMatch = Job(AgentJobStatus.Completed, actionKey: "curativa.audio.done");
        db.AgentJobs.AddRange(
            queuedMatch,
            executingMatch,
            pausedMatch,
            nullAction,
            queuedOther,
            completedMatch);
        await db.SaveChangesAsync();
        saveProbe.Reset();
        foreach (var job in new[] { queuedMatch, executingMatch, pausedMatch, nullAction, queuedOther, completedMatch })
        {
            cache.SetJobLogs(
                job.Id,
                [new AgentJobLogResponse("existing", JobLogLevels.Info, DateTimeOffset.UnixEpoch)]);
        }
        saveProbe.OnSaving = () =>
        {
            cache.TryGetJobLogs(queuedMatch.Id, out var duringSave).Should().BeTrue();
            duringSave!.Select(log => log.Message).Should().Equal("existing");
        };

        await controller.CancelStaleJobsByActionPrefixAsync("curativa.audio.");

        saveProbe.SaveCount.Should().Be(1);
        var jobs = await db.AgentJobs
            .Include(j => j.LogEntries)
            .ToDictionaryAsync(j => j.Id);
        jobs[queuedMatch.Id].Status.Should().Be(AgentJobStatus.Cancelled);
        jobs[executingMatch.Id].Status.Should().Be(AgentJobStatus.Cancelled);
        jobs[queuedMatch.Id].LogEntries.Should().ContainSingle(log =>
            log.Message == "Cancelled: stale from previous session."
            && log.Level == JobLogLevels.Warning);
        jobs[executingMatch.Id].LogEntries.Should().ContainSingle(log =>
            log.Message == "Cancelled: stale from previous session."
            && log.Level == JobLogLevels.Warning);
        jobs[pausedMatch.Id].Status.Should().Be(AgentJobStatus.Paused);
        jobs[nullAction.Id].Status.Should().Be(AgentJobStatus.Queued);
        jobs[queuedOther.Id].Status.Should().Be(AgentJobStatus.Queued);
        jobs[completedMatch.Id].Status.Should().Be(AgentJobStatus.Completed);
        cache.TryGetJobLogs(queuedMatch.Id, out var queuedLogs).Should().BeTrue();
        queuedLogs!.Select(log => log.Message).Should()
            .Equal("existing", "Cancelled: stale from previous session.");
        cache.TryGetJobLogs(pausedMatch.Id, out var pausedLogs).Should().BeTrue();
        pausedLogs!.Select(log => log.Message).Should().Equal("existing");
    }

    [Test]
    public async Task CancelStaleJobsByActionPrefixAsync_WhenNoStaleJobs_DoesNotSaveOrCache()
    {
        var cache = CreateCache();
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var controller = CreateController(db, cache);
        var job = Job(AgentJobStatus.Queued, actionKey: "other.action");
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();
        saveProbe.Reset();
        cache.SetJobLogs(
            job.Id,
            [new AgentJobLogResponse("existing", JobLogLevels.Info, DateTimeOffset.UnixEpoch)]);

        await controller.CancelStaleJobsByActionPrefixAsync("curativa.audio.");

        saveProbe.SaveCount.Should().Be(0);
        cache.TryGetJobLogs(job.Id, out var cached).Should().BeTrue();
        cached!.Select(log => log.Message).Should().Equal("existing");
    }

    [Test]
    public async Task CancelStaleJobsByActionPrefixAsync_WhenPrefixIsBlank_PreservesArgumentException()
    {
        await using var db = CreateDbContext(new SaveProbe());
        var controller = CreateController(db, CreateCache());

        var act = async () => await controller.CancelStaleJobsByActionPrefixAsync(" ");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Action key prefix is required. (Parameter 'actionKeyPrefix')");
    }

    private static HostAgentJobController CreateController(
        SharpClawDbContext db,
        ChatCache cache)
    {
        return new HostAgentJobController(
            jobs: null!,
            db,
            cache,
            new AgentJobAdministrationEngine(),
            new AgentJobLifecycleEngine());
    }

    private static SharpClawDbContext CreateDbContext(SaveProbe saveProbe)
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot())
            .AddInterceptors(saveProbe)
            .Options;
        return new SharpClawDbContext(options);
    }

    private static ChatCache CreateCache()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:CacheMaxBytes"] = "1048576"
            })
            .Build();
        return new ChatCache(configuration);
    }

    private static AgentJobDB Job(
        AgentJobStatus status,
        string? actionKey = "module.action",
        string? resultData = null) => new()
    {
        Id = Guid.NewGuid(),
        ChannelId = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        Status = status,
        ActionKey = actionKey,
        ResultData = resultData,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class SaveProbe : SaveChangesInterceptor
    {
        public int SaveCount { get; private set; }
        public Action? OnSaving { get; set; }

        public void Reset()
        {
            SaveCount = 0;
            OnSaving = null;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            OnSaving?.Invoke();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
