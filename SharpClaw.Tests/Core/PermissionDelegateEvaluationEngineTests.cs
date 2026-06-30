using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class PermissionDelegateEvaluationEngineTests
{
    [Test]
    public void TryEvaluateAsync_WhenDelegateIsUnknown_ReturnsNull()
    {
        var loadCount = 0;
        var engine = CreateEngine();

        var result = engine.TryEvaluateAsync(new(
            "MissingAsync",
            null,
            new ActionCaller(),
            CreateRegistry(),
            _ =>
            {
                loadCount++;
                return Task.FromResult(CreateEmptySnapshots());
            }));

        result.Should().BeNull();
        loadCount.Should().Be(0);
    }

    [Test]
    public void TryEvaluateAsync_WhenResourceDelegateHasNoResourceId_ReturnsNull()
    {
        var loadCount = 0;
        var engine = CreateEngine();

        var result = engine.TryEvaluateAsync(new(
            "UseDocumentAsync",
            null,
            new ActionCaller(),
            CreateRegistry(),
            _ =>
            {
                loadCount++;
                return Task.FromResult(CreateEmptySnapshots());
            }));

        result.Should().BeNull();
        loadCount.Should().Be(0);
    }

    [Test]
    public async Task TryEvaluateAsync_WhenGlobalFlagDelegateIsGranted_ReturnsApproved()
    {
        var loadCount = 0;
        var engine = CreateEngine();

        var resultTask = engine.TryEvaluateAsync(new(
            "CanAuditAsync",
            null,
            new ActionCaller(),
            CreateRegistry(),
            _ =>
            {
                loadCount++;
                return Task.FromResult(new PermissionDelegateSnapshotSet(
                    new PermissionSetSnapshot(
                        GlobalFlags:
                        [
                            new GlobalFlagPermissionGrant(
                                "CanAudit",
                                PermissionClearance.Independent)
                        ],
                        ResourceAccesses: [],
                        ClearanceUserWhitelist: new HashSet<Guid>(),
                        ClearanceAgentWhitelist: new HashSet<Guid>()),
                    ChannelPermissions: null,
                    ContextPermissions: null,
                    CallerPermissions: null));
            }));

        resultTask.Should().NotBeNull();
        var result = await resultTask!;
        result.Verdict.Should().Be(ClearanceVerdict.Approved);
        result.Reason.Should().Be("Agent can act independently.");
        loadCount.Should().Be(1);
    }

    [Test]
    public async Task TryEvaluateAsync_WhenResourceDelegateIsGranted_ReturnsApproved()
    {
        var resourceId = Guid.NewGuid();
        var loadCount = 0;
        var engine = CreateEngine();

        var resultTask = engine.TryEvaluateAsync(new(
            "UseDocumentAsync",
            resourceId,
            new ActionCaller(),
            CreateRegistry(),
            _ =>
            {
                loadCount++;
                return Task.FromResult(new PermissionDelegateSnapshotSet(
                    new PermissionSetSnapshot(
                        GlobalFlags: [],
                        ResourceAccesses:
                        [
                            new ResourcePermissionGrant(
                                "document",
                                resourceId,
                                PermissionClearance.Independent)
                        ],
                        ClearanceUserWhitelist: new HashSet<Guid>(),
                        ClearanceAgentWhitelist: new HashSet<Guid>()),
                    ChannelPermissions: null,
                    ContextPermissions: null,
                    CallerPermissions: null));
            }));

        resultTask.Should().NotBeNull();
        var result = await resultTask!;
        result.Verdict.Should().Be(ClearanceVerdict.Approved);
        result.Reason.Should().Be("Agent can act independently.");
        loadCount.Should().Be(1);
    }

    private static PermissionDelegateEvaluationEngine CreateEngine() =>
        new(new PermissionEvaluationEngine());

    private static PermissionDelegateSnapshotSet CreateEmptySnapshots() =>
        new(
            PermissionSetSnapshot.Empty,
            ChannelPermissions: null,
            ContextPermissions: null,
            CallerPermissions: null);

    private static ModuleRegistry CreateRegistry()
    {
        var registry = new ModuleRegistry();
        registry.Register(new PermissionDelegateModule());
        return registry;
    }

    private sealed class PermissionDelegateModule : ISharpClawCoreModule
    {
        private static readonly JsonElement EmptySchema =
            JsonDocument.Parse("{}").RootElement.Clone();

        public string Id => "permission_delegate";
        public string DisplayName => "Permission Delegate";
        public string ToolPrefix => "permdelegate";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
        [
            new(
                "audit",
                "Audit",
                EmptySchema,
                new ModuleToolPermission(
                    IsPerResource: false,
                    Check: null,
                    DelegateTo: "CanAuditAsync"))
        ];

        public IReadOnlyList<ModuleResourceTypeDescriptor>
            GetResourceTypeDescriptors() =>
        [
            new(
                "document",
                "Document",
                "UseDocumentAsync",
                (_, _) => Task.FromResult(new List<Guid>()))
        ];

        public IReadOnlyList<ModuleGlobalFlagDescriptor>
            GetGlobalFlagDescriptors() =>
        [
            new(
                "CanAudit",
                "Can Audit",
                "Allows audit operations.",
                "CanAuditAsync")
        ];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
