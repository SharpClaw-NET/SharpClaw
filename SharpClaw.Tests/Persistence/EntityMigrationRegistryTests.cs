using System.Text.Json;
using SharpClaw.Infrastructure.Persistence.JSON;

namespace SharpClaw.Tests.Persistence;

/// <summary>
/// Phase O groundwork tests: EntityMigrationRegistry — registration and lookup.
/// </summary>
[TestFixture]
public class EntityMigrationRegistryTests
{
    [Test]
    public void GetMigrators_NoRegistrations_ReturnsEmpty()
    {
        var registry = new EntityMigrationRegistry();
        Assert.That(registry.GetMigrators<FakeEntity>(fromVersion: 0), Is.Empty);
    }

    [Test]
    public void GetMigrators_FromVersionEqualsCurrent_ReturnsEmpty()
    {
        var registry = new EntityMigrationRegistry();
        registry.Register(new FakeMigrator(0, 1));
        Assert.That(registry.GetMigrators<FakeEntity>(fromVersion: JsonSchemaVersion.Current), Is.Empty);
    }

    [Test]
    public void GetMigrators_FromVersionBelowCurrent_ReturnsMigrator()
    {
        var registry = new EntityMigrationRegistry();
        var migrator = new FakeMigrator(0, 1);
        registry.Register(migrator);

        var result = registry.GetMigrators<FakeEntity>(fromVersion: 0);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.SameAs(migrator));
    }

    [Test]
    public void GetMigrators_MultipleMigrators_ReturnedInFromVersionOrder()
    {
        var registry = new EntityMigrationRegistry();
        var m1 = new FakeMigrator(0, 1);
        var m2 = new FakeMigrator(1, 2);
        // Register out of order intentionally.
        registry.Register(m2);
        registry.Register(m1);

        // Request from v0; both should appear ordered by FromVersion.
        var result = registry.GetMigrators<FakeEntity>(fromVersion: 0);

        Assert.That(result.Select(m => m.FromVersion), Is.EqualTo(new[] { 0, 1 }));
    }

    [Test]
    public void GetMigrators_DifferentEntityType_ReturnsEmpty()
    {
        var registry = new EntityMigrationRegistry();
        registry.Register(new FakeMigrator(0, 1));

        // Ask for a different entity type — should find nothing.
        Assert.That(registry.GetMigrators<OtherFakeEntity>(fromVersion: 0), Is.Empty);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class FakeEntity;
    private sealed class OtherFakeEntity;

    private sealed class FakeMigrator(int from, int to) : IEntityMigrator<FakeEntity>
    {
        public int FromVersion => from;
        public int ToVersion => to;
        public JsonElement Migrate(JsonElement source) => source;
    }
}
