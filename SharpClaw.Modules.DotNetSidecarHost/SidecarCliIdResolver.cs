using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Modules;

internal sealed class SidecarCliIdResolver : ICliIdResolver
{
    private static readonly JsonSerializerOptions JsonPrint = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<Guid, int> _guidToShort = [];
    private readonly Dictionary<int, Guid> _shortToGuid = [];
    private readonly object _gate = new();
    private int _nextId = 1;

    public Guid Resolve(string arg)
    {
        var normalized = arg.StartsWith('#') ? arg[1..] : arg;

        if (int.TryParse(normalized, out var shortId))
        {
            lock (_gate)
            {
                if (_shortToGuid.TryGetValue(shortId, out var guid))
                    return guid;
            }

            throw new ArgumentException($"Unknown short ID #{shortId}. Use 'list' to see available IDs.");
        }

        return Guid.Parse(arg);
    }

    public int GetOrAssign(Guid guid)
    {
        lock (_gate)
        {
            if (_guidToShort.TryGetValue(guid, out var existing))
                return existing;

            var id = _nextId++;
            _guidToShort[guid] = id;
            _shortToGuid[id] = guid;
            return id;
        }
    }

    public void PrintJson(object value)
    {
        var doc = JsonSerializer.SerializeToNode(value, JsonPrint);
        if (doc is null)
            return;

        InjectShortIds(doc);
        Console.WriteLine(doc.ToJsonString(JsonPrint));
    }

    private void InjectShortIds(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("Id", out var idNode)
                && idNode is not null
                && Guid.TryParse(idNode.ToString(), out var guid))
            {
                var shortId = GetOrAssign(guid);
                obj.Remove("#");
                var copy = new JsonObject { ["#"] = shortId };

                foreach (var kvp in obj.ToList())
                {
                    obj.Remove(kvp.Key);
                    copy[kvp.Key] = kvp.Value;
                }

                foreach (var kvp in copy.ToList())
                {
                    copy.Remove(kvp.Key);
                    obj[kvp.Key] = kvp.Value;
                }
            }

            foreach (var prop in obj.ToList())
            {
                if (prop.Value is not null)
                    InjectShortIds(prop.Value);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not null)
                    InjectShortIds(item);
            }
        }
    }
}
