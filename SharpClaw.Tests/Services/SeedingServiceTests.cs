using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Providers;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Tests.Services;

[TestFixture]
public sealed class SeedingServiceTests
{
    [Test]
    public async Task StartingAsync_WhenAnonymousUsernameIsMissing_FailsStartup()
    {
        await using var provider = CreateProvider(new Dictionary<string, string?>
        {
            ["Auth:AnonymousUsername"] = "missing-user"
        });
        var service = provider.GetRequiredService<SeedingService>();

        var act = async () => await service.StartingAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Auth:AnonymousUsername*missing-user*no matching user exists*");
    }

    [Test]
    public async Task StartingAsync_WhenAnonymousUsernameIsEmpty_FailsStartup()
    {
        await using var provider = CreateProvider(new Dictionary<string, string?>
        {
            ["Auth:AnonymousUsername"] = ""
        });
        var service = provider.GetRequiredService<SeedingService>();

        var act = async () => await service.StartingAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Auth:AnonymousUsername*empty*");
    }

    [Test]
    public async Task StartingAsync_WhenAnonymousUsernameExists_StartsSuccessfully()
    {
        await using var provider = CreateProvider(new Dictionary<string, string?>
        {
            ["Auth:AnonymousUsername"] = "anonymous"
        });
        await SeedUserAsync(provider, "anonymous", isAdmin: false);
        var service = provider.GetRequiredService<SeedingService>();

        await service.StartingAsync(CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        (await db.Users.AnyAsync(u => u.Username == "anonymous")).Should().BeTrue();
        (await db.Users.AnyAsync(u => u.Username == "admin" && u.IsUserAdmin)).Should().BeTrue();
    }

    private static ServiceProvider CreateProvider(IReadOnlyDictionary<string, string?> configurationOverrides)
    {
        var databaseName = $"SeedingServiceTests-{Guid.NewGuid():N}";
        var configurationValues = new Dictionary<string, string?>
        {
            ["Admin:Username"] = "admin",
            ["Admin:Password"] = "123456",
            ["Admin:ReconcilePermissions"] = "true"
        };

        foreach (var pair in configurationOverrides)
            configurationValues[pair.Key] = pair.Value;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton(sp => new ProviderApiClientFactory(
            Array.Empty<IProviderPlugin>(),
            sp.GetRequiredService<ModuleRegistry>()));
        services.AddDbContext<SharpClawDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<SeedingService>();

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task SeedUserAsync(
        ServiceProvider provider,
        string username,
        bool isAdmin)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        db.Users.Add(new UserDB
        {
            Username = username,
            PasswordHash = [1],
            PasswordSalt = [2],
            IsUserAdmin = isAdmin
        });

        await db.SaveChangesAsync();
    }
}
