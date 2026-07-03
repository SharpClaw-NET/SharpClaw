using FluentAssertions;
using JSONColdStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Application.Services;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Tests.Services;

[TestFixture]
public sealed class AuthServiceJsonColdStoreTests
{
    private string? _dataDirectory;

    [TearDown]
    public void TearDown()
    {
        if (_dataDirectory is not null && Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }

    [Test]
    public async Task TokenInvalidationBulkUpdatesRunAgainstJsonColdStore()
    {
        _dataDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "jsoncoldstore-auth-" + Guid.NewGuid().ToString("N"));

        var storageOptions = new JsonColdStoreStorageOptions
        {
            DataDirectory = _dataDirectory,
            EncryptAtRest = false,
        };

        var dbOptions = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseJsonColdStoreDatabase(
                storageOptions.DataDirectory,
                store => JsonColdStoreRegistration.ConfigureStore(store, storageOptions, null))
            .Options;

        await using var db = new SharpClawDbContext(dbOptions);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var target = new UserDB
        {
            Username = "target",
            PasswordHash = [],
            PasswordSalt = [],
        };
        var other = new UserDB
        {
            Username = "other",
            PasswordHash = [],
            PasswordSalt = [],
        };

        db.Users.AddRange(target, other);
        await db.SaveChangesAsync();

        db.RefreshTokens.AddRange(
            new RefreshTokenDB
            {
                Token = "target-active",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                UserId = target.Id,
            },
            new RefreshTokenDB
            {
                Token = "target-already-revoked",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                IsRevoked = true,
                UserId = target.Id,
            },
            new RefreshTokenDB
            {
                Token = "other-active",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                UserId = other.Id,
            });
        await db.SaveChangesAsync();

        var auth = new AuthService(
            db,
            new TokenService(new JwtOptions
            {
                Secret = Convert.ToBase64String(new byte[32]),
            }),
            new JwtOptions
            {
                Secret = Convert.ToBase64String(new byte[32]),
            },
            new ChatCache(new ConfigurationBuilder().Build()));

        await auth.InvalidateAccessTokensAsync([target.Id]);
        await auth.InvalidateRefreshTokensAsync([target.Id]);

        db.ChangeTracker.Clear();

        var reloadedTarget = await db.Users.SingleAsync(u => u.Id == target.Id);
        var reloadedOther = await db.Users.SingleAsync(u => u.Id == other.Id);
        var refreshTokens = await db.RefreshTokens
            .OrderBy(r => r.Token)
            .ToArrayAsync();

        reloadedTarget.AccessTokensInvalidatedAt.Should().BeAfter(DateTimeOffset.MinValue);
        reloadedOther.AccessTokensInvalidatedAt.Should().Be(DateTimeOffset.MinValue);
        refreshTokens.Single(r => r.Token == "target-active").IsRevoked.Should().BeTrue();
        refreshTokens.Single(r => r.Token == "target-already-revoked").IsRevoked.Should().BeTrue();
        refreshTokens.Single(r => r.Token == "other-active").IsRevoked.Should().BeFalse();
    }
}
