using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public sealed class ExecutionStorageSchemaPreflightTests
{
    private readonly List<string> _roots = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var root in _roots)
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        _roots.Clear();
    }

    [Test]
    public void EmptyJsonStore_CanBeMarkedAndRevalidated()
    {
        var root = CreateRoot();

        ExecutionStorageSchemaPreflight.ValidateJsonStoreBeforeInitialization(root);
        ExecutionStorageSchemaPreflight.MarkJsonStoreInitialized(root);
        ExecutionStorageSchemaPreflight.ValidateJsonStoreBeforeInitialization(root);

        Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [TestCase("AgentJobLogEntryDB")]
    public void LegacyDiagnosticCollection_RequiresAnExplicitUpgrade(
        string collectionName)
    {
        var root = CreateRoot();
        WriteLegacyRecord(root, collectionName);

        var action = () =>
            ExecutionStorageSchemaPreflight.ValidateJsonStoreBeforeInitialization(root);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*upgrade is required*")
            .WithMessage("*No migration or compatibility fallback*")
            .WithMessage($"*{collectionName}*");
    }

    [TestCase("AgentJobDB")]
    public void UnversionedExecutionMetadata_RequiresAnExplicitUpgrade(
        string collectionName)
    {
        var root = CreateRoot();
        WriteLegacyRecord(root, collectionName);

        var action = () =>
            ExecutionStorageSchemaPreflight.ValidateJsonStoreBeforeInitialization(root);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*unversioned execution records are present*")
            .WithMessage("*upgrade the store before starting this version*");
    }

    [Test]
    public void VersionMarker_AllowsCompactExecutionMetadataButNotLegacyDiagnostics()
    {
        var root = CreateRoot();
        ExecutionStorageSchemaPreflight.MarkJsonStoreInitialized(root);
        WriteLegacyRecord(root, "AgentJobDB");

        ExecutionStorageSchemaPreflight.ValidateJsonStoreBeforeInitialization(root);

        WriteLegacyRecord(root, "AgentJobLogEntryDB");
        var action = () =>
            ExecutionStorageSchemaPreflight.ValidateJsonStoreBeforeInitialization(root);
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*legacy diagnostic collections are still present*");
    }

    private string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.Tests",
            "execution-schema",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static void WriteLegacyRecord(string root, string collectionName)
    {
        var collection = Path.Combine(root, collectionName);
        Directory.CreateDirectory(collection);
        File.WriteAllText(Path.Combine(collection, Guid.NewGuid() + ".json"), "{}");
    }
}
