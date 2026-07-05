using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Modules.Foreign;

namespace SharpClaw.Runtime.BLL.Modules.Foreign;

internal static class ForeignModuleProtocolAdapters
{
    private static readonly JsonSerializerOptions CliJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ModuleHeaderTag ToModuleHeaderTag(
        this ForeignModuleHeaderTagDescriptor descriptor,
        ModuleManifest manifest,
        ForeignModuleProtocolClient client)
    {
        var tag = new ModuleHeaderTag(
            descriptor.Name,
            async (_, ct) => (await client.ResolveHeaderTagAsync(
                manifest,
                descriptor.Name,
                context: null,
                ct)).Value);

        return descriptor.SupportsContext
            ? tag with
            {
                ResolveWithContext = async (_, context, ct) =>
                    (await client.ResolveHeaderTagAsync(
                        manifest,
                        descriptor.Name,
                        context,
                        ct)).Value
            }
            : tag;
    }

    public static ModuleResourceTypeDescriptor ToModuleResourceTypeDescriptor(
        this ForeignModuleResourceTypeDescriptor descriptor,
        ModuleManifest manifest,
        ForeignModuleProtocolClient client) =>
        new(
            descriptor.ResourceType,
            descriptor.GrantLabel,
            descriptor.DelegateMethodName,
            async (_, ct) => await client.LoadResourceIdsAsync(
                manifest,
                descriptor.ResourceType,
                ct),
            descriptor.SupportsLookupItems
                ? async (_, ct) =>
                {
                    var items = await client.LoadResourceLookupItemsAsync(
                        manifest,
                        descriptor.ResourceType,
                        ct);
                    return [.. items.Select(item => (item.Id, item.Name))];
                }
                : null,
            descriptor.DefaultResourceKey);

    public static ModuleCliCommand ToModuleCliCommand(
        this ForeignModuleCliCommandDescriptor descriptor,
        ModuleManifest manifest,
        ForeignModuleProtocolClient client) =>
        new(
            descriptor.Name,
            descriptor.Aliases ?? [],
            descriptor.Scope,
            descriptor.Description,
            descriptor.UsageLines ?? [],
            async (args, sp, ct) =>
            {
                var result = await client.ExecuteCliCommandAsync(
                    manifest,
                    descriptor.Name,
                    args,
                    ct);

                if (!string.IsNullOrEmpty(result.Stdout))
                    WriteStdout(result.Stdout, sp.GetService(typeof(ICliIdResolver)) as ICliIdResolver);
                if (!string.IsNullOrEmpty(result.Stderr))
                    Console.Error.Write(result.Stderr);
            });

    private static void WriteStdout(string stdout, ICliIdResolver? ids)
    {
        if (ids is null)
        {
            Console.Out.Write(stdout);
            return;
        }

        Console.Out.Write(RewriteJsonShortIds(stdout, ids));
    }

    private static string RewriteJsonShortIds(string text, ICliIdResolver ids)
    {
        var rewritten = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var start = FindNextJsonStart(text, index);
            if (start < 0)
            {
                rewritten.Append(text, index, text.Length - index);
                break;
            }

            rewritten.Append(text, index, start - index);

            if (!TryFindJsonEnd(text, start, out var end))
            {
                rewritten.Append(text, start, text.Length - start);
                break;
            }

            var raw = text[start..(end + 1)];
            try
            {
                var node = JsonNode.Parse(raw);
                if (node is null)
                {
                    rewritten.Append(raw);
                }
                else
                {
                    InjectShortIds(node, ids);
                    rewritten.Append(node.ToJsonString(CliJsonOptions));
                }
            }
            catch (JsonException)
            {
                rewritten.Append(raw);
            }

            index = end + 1;
        }

        return rewritten.ToString();
    }

    private static int FindNextJsonStart(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] is '{' or '[')
                return i;
        }

        return -1;
    }

    private static bool TryFindJsonEnd(string text, int start, out int end)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch is '{' or '[')
            {
                depth++;
                continue;
            }

            if (ch is not ('}' or ']'))
                continue;

            depth--;
            if (depth == 0)
            {
                end = i;
                return true;
            }
        }

        end = -1;
        return false;
    }

    private static void InjectShortIds(JsonNode node, ICliIdResolver ids)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("Id", out var idNode)
                && idNode is not null
                && Guid.TryParse(idNode.ToString(), out var guid))
            {
                var shortId = ids.GetOrAssign(guid);
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
                    InjectShortIds(prop.Value, ids);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not null)
                    InjectShortIds(item, ids);
            }
        }
    }
}
