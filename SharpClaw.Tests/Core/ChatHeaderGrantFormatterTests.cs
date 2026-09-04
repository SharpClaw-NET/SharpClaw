using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatHeaderGrantFormatterTests
{
    [Test]
    public void FormatGrantNames_WhenGlobalFlagStartsWithCan_TrimsPrefix()
    {
        var formatter = new ChatHeaderGrantFormatter(new RegistrationCatalog());
        var permissionSet = new PermissionSetState
        {
            GlobalFlags =
            [
                new GlobalFlagState
                {
                    FlagKey = "CanReadLogs",
                    Clearance = PermissionClearance.Independent
                }
            ]
        };

        var grants = formatter.FormatGrantNames(permissionSet);

        grants.Should().Equal("ReadLogs");
    }

    [Test]
    public async Task FormatGrantNamesWithResourcesAsync_WhenWildcardGrantExists_ExpandsResourceIds()
    {
        var firstId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var secondId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var registry = new RegistrationCatalog();
        registry.Register(new ResourceRegistration(
            new RegistrationResourceTypeDescriptor(
                "documents",
                "Documents",
                "CanUseDocuments",
                (_, _) => Task.FromResult(new List<Guid>
                {
                    firstId,
                    secondId
                }))));
        var formatter = new ChatHeaderGrantFormatter(registry);
        var permissionSet = new PermissionSetState
        {
            ResourceAccesses =
            [
                new ResourceAccessState
                {
                    ResourceType = "documents",
                    ResourceId = WellKnownIds.AllResources,
                    Clearance = PermissionClearance.Independent
                }
            ]
        };

        using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var grants = await formatter.FormatGrantNamesWithResourcesAsync(
            permissionSet,
            serviceProvider,
            CancellationToken.None);

        grants.Should().Equal($"Documents[{firstId:D},{secondId:D}]");
    }

    private sealed class ResourceRegistration(RegistrationResourceTypeDescriptor descriptor)
        : ISharpClawCoreRegistration
    {
        public string Id => "grant_test";
        public string DisplayName => "Grant Test";
        public string ToolPrefix => "granttest";
        public void ConfigureServices(IServiceCollection services) { }
        public IReadOnlyList<RegistrationToolDefinition> GetToolDefinitions() => [];
        public IReadOnlyList<RegistrationResourceTypeDescriptor> GetResourceTypeDescriptors()
            => [descriptor];

        public Task<string> ExecuteToolAsync(
            string toolName,
            System.Text.Json.JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct)
        {
            return Task.FromResult("");
        }
    }
}
