using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class HeaderTagProcessor(
    SharpClawDbContext db,
    ChatHeaderTemplateEngine headerTemplates,
    ChatHeaderExpansionPlanner headerExpansionPlanner,
    IServiceProvider serviceProvider,
    IConfiguration configuration)
{
    private readonly bool _disableModuleHeaderTags =
        configuration.GetValue<bool>("Chat:DisableModuleHeaderTags");

    private readonly bool _disableHeaderTagExpansion =
        configuration.GetValue<bool>("Chat:DisableHeaderTagExpansion");

    private readonly IChatHeaderResourceTagResolver _resourceTags =
        new EfHeaderResourceTagResolver(db);

    public async Task<string> ExpandAsync(
        string template,
        ChannelDB channel,
        AgentDB agent,
        string clientType,
        Guid? userId,
        CancellationToken ct,
        CompletionParameters? completionParameters = null,
        string providerKey = "")
    {
        var options = new ChatHeaderExpansionOptions(
            DisableHeaderTagExpansion: _disableHeaderTagExpansion,
            DisableModuleHeaderTags: _disableModuleHeaderTags);

        if (options.DisableHeaderTagExpansion)
            return template;

        var plan = headerExpansionPlanner.BuildPlan(
            template,
            userId,
            options);
        if (!plan.ShouldExpand)
            return template;

        var context = await BuildContextAsync(
            channel,
            agent,
            clientType,
            userId,
            plan,
            ct,
            completionParameters,
            providerKey);

        return await headerTemplates.ExpandAsync(
            template,
            context,
            options,
            _resourceTags,
            serviceProvider,
            ct);
    }

    private async Task<ChatHeaderExpansionContext> BuildContextAsync(
        ChannelDB channel,
        AgentDB agent,
        string clientType,
        Guid? userId,
        ChatHeaderExpansionPlan plan,
        CancellationToken ct,
        CompletionParameters? completionParameters = null,
        string providerKey = "")
    {
        UserDB? user = null;
        PermissionSetDB? userPs = null;
        if (plan.RequiresUser)
        {
            user = await db.Users
                .AsNoTracking()
                .Include(u => u.Role).ThenInclude(r => r!.PermissionSet)
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (plan.RequiresUserPermissionSet
                && user?.Role?.PermissionSetId is { } psId)
            {
                userPs = await db.PermissionSets
                    .AsNoTracking()
                    .Include(p => p.GlobalFlags)
                    .Include(p => p.ResourceAccesses)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(p => p.Id == psId, ct);
            }
        }

        RoleDB? agentRole = null;
        PermissionSetDB? agentPs = null;
        if (plan.RequiresAgentPermissionSet)
        {
            var agentWithRole = await db.Agents
                .AsNoTracking()
                .Include(a => a.Role).ThenInclude(r => r!.PermissionSet)
                .FirstOrDefaultAsync(a => a.Id == agent.Id, ct);

            agentRole = agentWithRole?.Role;
            if (agentRole?.PermissionSetId is { } agentPsId)
            {
                agentPs = await db.PermissionSets
                    .AsNoTracking()
                    .Include(p => p.ResourceAccesses)
                    .Include(p => p.GlobalFlags)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(p => p.Id == agentPsId, ct);
            }
        }

        return new ChatHeaderExpansionContext(
            channel,
            agent,
            clientType,
            user,
            userPs,
            agentRole,
            agentPs,
            completionParameters,
            providerKey);
    }

    private sealed class EfHeaderResourceTagResolver(SharpClawDbContext db)
        : IChatHeaderResourceTagResolver
    {
        public async Task<IReadOnlyList<BaseEntity>?> LoadEntitiesAsync(
            string tagName,
            CancellationToken ct)
        {
            return tagName.ToLowerInvariant() switch
            {
                "agents" => Cast(await db.Agents.AsNoTracking().ToListAsync(ct)),
                "models" => Cast(await db.Models
                    .AsNoTracking()
                    .Include(m => m.Provider)
                    .ToListAsync(ct)),
                "providers" => Cast(await db.Providers.AsNoTracking().ToListAsync(ct)),
                "channels" => Cast(await db.Channels.AsNoTracking().ToListAsync(ct)),
                "threads" => Cast(await db.ChatThreads.AsNoTracking().ToListAsync(ct)),
                "roles" => Cast(await db.Roles.AsNoTracking().ToListAsync(ct)),
                "users" => Cast(await db.Users.AsNoTracking().ToListAsync(ct)),
                "tasks" or "taskdefinitions" => Cast(await db.TaskDefinitions
                    .AsNoTracking()
                    .ToListAsync(ct)),
                _ => null
            };
        }

        private static IReadOnlyList<BaseEntity> Cast<T>(List<T> items)
            where T : BaseEntity
        {
            return items.ConvertAll(static item => (BaseEntity)item);
        }
    }
}
