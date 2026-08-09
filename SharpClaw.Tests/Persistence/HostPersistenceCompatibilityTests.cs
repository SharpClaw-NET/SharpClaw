using System.Security.Cryptography;
using System.Text.Json;
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
    public async Task JsonColdStore_ReopensImmutablePreCutoverStoreWithCurrentEntityIdentity()
    {
        const string legacyEntityNamespace = "SharpClaw.Contracts.Entities.Core";
        typeof(AgentDB).FullName.Should().Be($"{legacyEntityNamespace}.AgentDB");
        typeof(AgentJobDB).FullName.Should().Be($"{legacyEntityNamespace}.Jobs.AgentJobDB");
        typeof(ChatMessageDB).FullName.Should().Be($"{legacyEntityNamespace}.Messages.ChatMessageDB");
        typeof(RoleDB).FullName.Should().Be($"{legacyEntityNamespace}.Clearance.RoleDB");

        var fixtureDirectory = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Persistence",
            "Fixtures",
            "PreCutoverJsonColdStore");
        var manifestPath = Path.Combine(fixtureDirectory, "fixture-manifest.json");
        var manifest = JsonSerializer.Deserialize<PreCutoverFixtureManifest>(
            await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The pre-cutover fixture manifest is empty.");

        manifest.ProducerCommit.Should().Be("a9a01bee8487c24a10e8bb5ea996825077a83158");
        manifest.ContractsPackage.Should().Be("SharpClaw.Contracts 0.5.0-alpha.3");
        manifest.ProviderPackage.Should().Be("Supprocom.JSONColdStore 0.1.0-alpha.10");

        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(fixtureDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue(file.Path);
            new FileInfo(path).Length.Should().Be(file.Length, file.Path);
            await using var stream = File.OpenRead(path);
            Convert.ToHexString(await SHA256.HashDataAsync(stream))
                .Should().Be(file.Sha256, file.Path);
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.Tests",
            "host-persistence-compatibility",
            Guid.NewGuid().ToString("N"));
        CopyDirectory(fixtureDirectory, root);

        try
        {
            await using var reopened = CreateDbContext(root);
            var persisted = await reopened.Agents
                .AsNoTracking()
                .SingleAsync(agent => agent.Id == manifest.ExpectedRecords.AgentId);

            persisted.Name.Should().Be("pre-cutover-agent");
            persisted.ModelId.Should().Be(manifest.ExpectedRecords.ModelId);
            (await reopened.Models.AsNoTracking().SingleAsync(model => model.Id == manifest.ExpectedRecords.ModelId))
                .ProviderId.Should().Be(manifest.ExpectedRecords.ProviderId);
            (await reopened.Providers.AsNoTracking().SingleAsync(provider => provider.Id == manifest.ExpectedRecords.ProviderId))
                .ProviderKey.Should().Be("pre-cutover-provider");
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

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private sealed record PreCutoverFixtureManifest(
        string ProducerRepository,
        string ProducerCommit,
        string ContractsPackage,
        string ContractsNupkgSha256,
        string ContractsDllSha256,
        string ProviderPackage,
        string ProviderNupkgSha256,
        string ProviderDllSha256,
        ExpectedRecords ExpectedRecords,
        IReadOnlyList<FixtureFile> Files);

    private sealed record ExpectedRecords(Guid ProviderId, Guid ModelId, Guid AgentId);

    private sealed record FixtureFile(string Path, long Length, string Sha256);
}
