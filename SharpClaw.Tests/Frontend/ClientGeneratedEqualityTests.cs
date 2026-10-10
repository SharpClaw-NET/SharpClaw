using System.Diagnostics.CodeAnalysis;
using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

[SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this fixture through reflection.")]
[TestFixture]
internal sealed class ClientGeneratedEqualityTests
{
    [Test]
    public void ActionCoverageRetainsGeneratedKeyEqualityAndCompleteRecordEquality()
    {
        var first = new ClientActionCoverageEntry("entry", "receive", ClientActionCatalog.CommandReceive);
        var updated = first with { Boundary = "complete", ActionKey = ClientActionCatalog.CommandComplete };
        var anotherId = updated with { Id = "another-entry" };
        // These assignments verify the generated interface contract at compile time.
        // Concrete variable types would stop testing that compatibility boundary.
#pragma warning disable CA1859
        global::Uno.Extensions.Equality.IKeyEquatable<ClientActionCoverageEntry> firstKey = first;
        global::Uno.Extensions.Equality.IKeyEquatable<ClientActionCoverageEntry> updatedKey = updated;
#pragma warning restore CA1859

        firstKey.KeyEquals(updated).Should().BeTrue();
        updatedKey.KeyEquals(first).Should().BeTrue();
        firstKey.GetKeyHashCode().Should().Be(updatedKey.GetKeyHashCode());
        firstKey.KeyEquals(anotherId).Should().BeFalse();
        first.Equals(updated).Should().BeFalse("ordinary equality includes Boundary and ActionKey");
        first.Equals(first with { Boundary = "complete" }).Should().BeFalse();
        first.Equals(first with { ActionKey = ClientActionCatalog.CommandComplete }).Should().BeFalse();
        first.Equals(first with { Id = "another-entry" }).Should().BeFalse();
        first.Equals(first with { }).Should().BeTrue();
    }
}
