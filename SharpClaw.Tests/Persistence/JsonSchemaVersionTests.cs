using System.Text;
using System.Text.Json;
using SharpClaw.Infrastructure.Persistence.JSON;

namespace SharpClaw.Tests.Persistence;

/// <summary>
/// Phase O groundwork tests: JsonSchemaVersion — $schemaVersion field injection and reading.
/// </summary>
[TestFixture]
public class JsonSchemaVersionTests
{
    // ── Inject ───────────────────────────────────────────────────────────────

    [Test]
    public void Inject_NormalObject_ProducesValidJson()
    {
        var input = Encoding.UTF8.GetBytes("""{"id":"abc","value":42}""");
        var result = JsonSchemaVersion.Inject(input);
        var doc = JsonDocument.Parse(result);
        Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
    }

    [Test]
    public void Inject_NormalObject_ContainsSchemaVersionField()
    {
        var input = Encoding.UTF8.GetBytes("""{"id":"abc","value":42}""");
        var result = JsonSchemaVersion.Inject(input);
        var doc = JsonDocument.Parse(result);
        Assert.That(doc.RootElement.TryGetProperty("$schemaVersion", out var prop), Is.True);
        Assert.That(prop.GetInt32(), Is.EqualTo(JsonSchemaVersion.Current));
    }

    [Test]
    public void Inject_NormalObject_SchemaVersionIsFirstProperty()
    {
        var input = Encoding.UTF8.GetBytes("""{"id":"abc","value":42}""");
        var result = JsonSchemaVersion.Inject(input);
        var doc = JsonDocument.Parse(result);
        var first = doc.RootElement.EnumerateObject().First();
        Assert.That(first.Name, Is.EqualTo("$schemaVersion"));
    }

    [Test]
    public void Inject_NormalObject_PreservesOriginalProperties()
    {
        var input = Encoding.UTF8.GetBytes("""{"id":"abc","value":42}""");
        var result = JsonSchemaVersion.Inject(input);
        var doc = JsonDocument.Parse(result);
        Assert.That(doc.RootElement.GetProperty("id").GetString(), Is.EqualTo("abc"));
        Assert.That(doc.RootElement.GetProperty("value").GetInt32(), Is.EqualTo(42));
    }

    [Test]
    public void Inject_IndentedObject_ProducesValidJson()
    {
        var input = Encoding.UTF8.GetBytes("{\n  \"id\": \"abc\",\n  \"value\": 42\n}");
        var result = JsonSchemaVersion.Inject(input);
        var doc = JsonDocument.Parse(result);
        Assert.That(doc.RootElement.TryGetProperty("$schemaVersion", out _), Is.True);
        Assert.That(doc.RootElement.GetProperty("id").GetString(), Is.EqualTo("abc"));
    }

    [Test]
    public void Inject_EmptyObject_ContainsOnlySchemaVersion()
    {
        var input = Encoding.UTF8.GetBytes("{}");
        var result = JsonSchemaVersion.Inject(input);
        var doc = JsonDocument.Parse(result);
        var properties = doc.RootElement.EnumerateObject().ToList();
        Assert.That(properties, Has.Count.EqualTo(1));
        Assert.That(properties[0].Name, Is.EqualTo("$schemaVersion"));
    }

    [Test]
    public void Current_IsOne()
    {
        Assert.That(JsonSchemaVersion.Current, Is.EqualTo(1));
    }

    // ── ReadFrom ─────────────────────────────────────────────────────────────

    [Test]
    public void ReadFrom_JsonWithSchemaVersion_ReturnsVersion()
    {
        var json = """{"$schemaVersion":1,"id":"abc"}""";
        Assert.That(JsonSchemaVersion.ReadFrom(json), Is.EqualTo(1));
    }

    [Test]
    public void ReadFrom_JsonWithoutSchemaVersion_ReturnsZero()
    {
        var json = """{"id":"abc","value":42}""";
        Assert.That(JsonSchemaVersion.ReadFrom(json), Is.EqualTo(0));
    }

    [Test]
    public void ReadFrom_EmptyObject_ReturnsZero()
    {
        Assert.That(JsonSchemaVersion.ReadFrom("{}"), Is.EqualTo(0));
    }

    [Test]
    public void ReadFrom_MalformedJson_ReturnsZero()
    {
        Assert.That(JsonSchemaVersion.ReadFrom("{not valid"), Is.EqualTo(0));
    }

    [Test]
    public void ReadFrom_RoundTrip_InjectThenRead_ReturnsCurrent()
    {
        var input = Encoding.UTF8.GetBytes("""{"id":"abc"}""");
        var injected = JsonSchemaVersion.Inject(input);
        var json = Encoding.UTF8.GetString(injected);
        Assert.That(JsonSchemaVersion.ReadFrom(json), Is.EqualTo(JsonSchemaVersion.Current));
    }
}
