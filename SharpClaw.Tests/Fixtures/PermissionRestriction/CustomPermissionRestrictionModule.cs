using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.TestFixtures.CustomPermissionPolicy;

public sealed class CustomPermissionRestrictionModule : ISharpClawModule
{
    public const string SourceId = "sharpclaw_test_permission_restriction";

    public ModuleIdentity Identity { get; } = new(
        SourceId,
        "Tracked Permission Restriction",
        "test_permission");

    public void ConfigureServices(IServiceCollection services) =>
        services.AddAuthorizationRestriction<RoleAuthorizationRestriction>(
            "tracked-role-boundary");
}

public sealed class RoleAuthorizationRestriction : IAuthorizationRestriction
{
    public const string DenyRole = "test-permission-restriction-deny";

    public ValueTask<AuthorizationRestriction> EvaluateAsync(
        ActionContext<AuthorizationRequest> context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var denied = context.Caller.Roles?.Any(role =>
            string.Equals(role, DenyRole, StringComparison.Ordinal)) == true;
        return ValueTask.FromResult(denied
            ? AuthorizationRestriction.Deny(
                "tracked_role_denied",
                "The tracked permission restriction denies this caller.")
            : AuthorizationRestriction.Preserve());
    }
}
