using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class RegistrationServiceScopeTests
{
    [Test]
    public void GetRequiredService_WhenServiceTypeIsBlocked_ThrowsBeforeInnerProviderResolves()
    {
        var services = new ServiceCollection();
        services.AddSingleton<BlockedService>();
        var provider = services.BuildServiceProvider();
        var scope = new RegistrationServiceScope(
            provider,
            "test_registration",
            [typeof(BlockedService)]);

        var act = () => scope.GetRequiredService<BlockedService>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*test_registration*blocked service*BlockedService*");
    }

    [Test]
    public void GetService_WhenServiceTypeIsAllowed_ForwardsToInnerProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AllowedService>();
        var provider = services.BuildServiceProvider();
        var scope = new RegistrationServiceScope(
            provider,
            "test_registration",
            [typeof(BlockedService)]);

        scope.GetService(typeof(AllowedService))
            .Should()
            .BeSameAs(provider.GetRequiredService<AllowedService>());
    }

    [Test]
    public void GetRequiredKeyedService_WhenServiceTypeIsBlocked_ThrowsBeforeKeyedResolution()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<BlockedService>("key");
        var provider = services.BuildServiceProvider();
        var scope = new RegistrationServiceScope(
            provider,
            "test_registration",
            [typeof(BlockedService)]);

        var act = () => scope.GetRequiredKeyedService(typeof(BlockedService), "key");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*test_registration*blocked service*BlockedService*");
    }

    private sealed class AllowedService;

    private sealed class BlockedService;
}
