using JSONColdStore;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public sealed class HostPersistenceCompatibilityTests
{
    [Test]
    public async Task JsonColdStore_ReopensPopulatedStoreWithPreCutoverEntityIdentity()
    {
        const string legacyEntityNamespace = "SharpClaw.Contracts.Entities.Core";
        typeof(AgentDB).FullName.Should().Be($"{legacyEntityNamespace}.AgentDB");
        typeof(AgentJobDB).FullName.Should().Be($"{legacyEntityNamespace}.Jobs.AgentJobDB");
        typeof(ChatMessageDB).FullName.Should().Be($"{legacyEntityNamespace}.Messages.ChatMessageDB");
        typeof(RoleDB).FullName.Should().Be($"{legacyEntityNamespace}.Clearance.RoleDB");

        var root = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.Tests",
            "host-persistence-compatibility",
            Guid.NewGuid().ToString("N"));
        var providerId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        try
        {
            await using (var db = CreateDbContext(root))
            {
                await db.Database.EnsureCreatedAsync();

                var provider = new ProviderDB
                {
                    Id = providerId,
                    Name = "compatibility-provider",
                    ProviderKey = "compatibility-provider",
                };
                var model = new ModelDB
                {
                    Id = modelId,
                    Name = "compatibility-model",
                    ProviderId = providerId,
                    Provider = provider,
                };
                var agent = new AgentDB
                {
                    Id = agentId,
                    Name = "compatibility-agent",
                    ModelId = modelId,
                    Model = model,
                };

                db.Providers.Add(provider);
                db.Models.Add(model);
                db.Agents.Add(agent);
                await db.SaveChangesAsync();
            }

            await using (var reopened = CreateDbContext(root))
            {
                var persisted = await reopened.Agents
                    .AsNoTracking()
                    .SingleAsync(agent => agent.Id == agentId);

                persisted.Name.Should().Be("compatibility-agent");
                persisted.ModelId.Should().Be(modelId);
                (await reopened.Models.AsNoTracking().SingleAsync(model => model.Id == modelId))
                    .ProviderId.Should().Be(providerId);
                (await reopened.Providers.AsNoTracking().SingleAsync(provider => provider.Id == providerId))
                    .ProviderKey.Should().Be("compatibility-provider");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AgentModelRelationship_RestrictsModelDeletion()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.Tests",
            "host-persistence-delete-policy",
            Guid.NewGuid().ToString("N"));

        try
        {
            using var db = CreateDbContext(root);
            var foreignKey = db.Model
                .FindEntityType(typeof(AgentDB))!
                .GetForeignKeys()
                .Single(key => key.Properties.Count == 1
                    && key.Properties[0].Name == nameof(AgentDB.ModelId));

            foreignKey.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static SharpClawDbContext CreateDbContext(string root)
    {
        var storageOptions = new JsonColdStoreStorageOptions
        {
            DataDirectory = root,
            EncryptAtRest = false,
        };
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseJsonColdStoreDatabase(
                root,
                store => JsonColdStoreRegistration.ConfigureStore(store, storageOptions, null))
            .Options;
        return new SharpClawDbContext(options);
    }
}
