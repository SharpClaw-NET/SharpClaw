using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SharpClaw.Application.Core.Modules;

namespace SharpClaw.Modules.ModuleDev.Services;

/// <summary>
/// Generates module project files from a specification using embedded templates.
/// </summary>
internal sealed partial class ModuleScaffoldService(
    ModuleWorkspaceService workspace,
    DevEnvironmentService devEnv,
    ModuleRegistry registry)
{
    /// <summary>
    /// Scaffold specification provided by the agent.
    /// </summary>
    internal sealed record ScaffoldSpec(
        string ModuleId,
        string DisplayName,
        string ToolPrefix,
        string? Description = null,
        IReadOnlyList<ToolStub>? Tools = null,
        IReadOnlyList<string>? ContractsRequired = null,
        IReadOnlyList<string>? ContractsExported = null,
        IReadOnlyList<string>? Platforms = null);

    internal sealed record ToolStub(
        string Name,
        string? Description = null,
        string? ParametersHint = null);

    /// <summary>
    /// Scaffold result returned to the caller.
    /// </summary>
    internal sealed record ScaffoldResult(string ModuleDir, IReadOnlyList<string> Files);

    /// <summary>
    /// Generate a complete module project from a spec.
    /// </summary>
    public async Task<ScaffoldResult> ScaffoldAsync(ScaffoldSpec spec, CancellationToken ct = default)
    {
        ValidateSpec(spec);

        var moduleDir = workspace.ResolveModuleDir(spec.ModuleId);
        Directory.CreateDirectory(moduleDir);

        var files = new List<string>();

        // 1. Generate .csproj
        var csprojContent = LoadTemplate("ProjectFile.csproj.template")
            .Replace("{{CONTRACTS_PATH}}", devEnv.ContractsAssemblyPath);

        var csprojName = ToPascalCase(spec.ModuleId) + ".csproj";
        await WriteFileAsync(moduleDir, csprojName, csprojContent, ct);
        files.Add(csprojName);

        // 2. Generate module class
        var className = ToPascalCase(spec.ModuleId) + "Module";
        var ns = ToPascalCase(spec.ModuleId);
        var toolStubs = BuildToolStubs(spec.Tools);
        var toolDispatch = BuildToolDispatch(spec.Tools);

        var moduleContent = LoadTemplate("ModuleClass.cs.template")
            .Replace("{{NAMESPACE}}", ns)
            .Replace("{{CLASS_NAME}}", className)
            .Replace("{{MODULE_ID}}", spec.ModuleId)
            .Replace("{{DISPLAY_NAME}}", spec.DisplayName)
            .Replace("{{TOOL_PREFIX}}", spec.ToolPrefix)
            .Replace("{{TOOL_STUBS}}", toolStubs)
            .Replace("{{TOOL_DISPATCH}}", toolDispatch);

        var moduleFileName = className + ".cs";
        await WriteFileAsync(moduleDir, moduleFileName, moduleContent, ct);
        files.Add(moduleFileName);

        // 3. Generate module.json
        var assemblyName = ToPascalCase(spec.ModuleId);
        var manifestContent = LoadTemplate("Manifest.json.template")
            .Replace("{{MODULE_ID}}", spec.ModuleId)
            .Replace("{{DISPLAY_NAME}}", spec.DisplayName)
            .Replace("{{TOOL_PREFIX}}", spec.ToolPrefix)
            .Replace("{{ASSEMBLY_NAME}}", assemblyName)
            .Replace("{{DESCRIPTION}}", spec.Description ?? "");

        await WriteFileAsync(moduleDir, "module.json", manifestContent, ct);
        files.Add("module.json");

        return new ScaffoldResult(moduleDir, files);
    }

    // ── Validation ────────────────────────────────────────────────

    private void ValidateSpec(ScaffoldSpec spec)
    {
        if (!ModuleIdRegex().IsMatch(spec.ModuleId))
            throw new ArgumentException(
                $"Invalid module ID '{spec.ModuleId}'. Must match ^[a-z][a-z0-9_]{{0,39}}$.");

        if (!ToolPrefixRegex().IsMatch(spec.ToolPrefix))
            throw new ArgumentException(
                $"Invalid tool prefix '{spec.ToolPrefix}'. Must match ^[a-z][a-z0-9]{{0,19}}$.");

        if (string.IsNullOrWhiteSpace(spec.DisplayName))
            throw new ArgumentException("Display name is required.");

        // Check uniqueness against loaded modules
        if (registry.GetModule(spec.ModuleId) is not null)
            throw new InvalidOperationException(
                $"Module ID '{spec.ModuleId}' is already registered.");

        if (registry.GetModuleByPrefix(spec.ToolPrefix) is not null)
            throw new InvalidOperationException(
                $"Tool prefix '{spec.ToolPrefix}' is already in use.");
    }

    // ── Template helpers ──────────────────────────────────────────

    private static string LoadTemplate(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{assembly.GetName().Name}.Templates.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded template not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string BuildToolStubs(IReadOnlyList<ToolStub>? tools)
    {
        if (tools is null or { Count: 0 })
            return "        // No tools defined — add ModuleToolDefinition entries here";

        var sb = new StringBuilder();
        foreach (var tool in tools)
        {
            var desc = tool.Description ?? $"TODO: describe {tool.Name}";
            sb.AppendLine($"        new(\"{tool.Name}\",");
            sb.AppendLine($"            \"{EscapeString(desc)}\",");
            sb.AppendLine($"            EmptySchema(),");
            sb.AppendLine($"            new ModuleToolPermission(IsPerResource: false, Check: null, DelegateTo: null)),");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildToolDispatch(IReadOnlyList<ToolStub>? tools)
    {
        if (tools is null or { Count: 0 })
            return "            // No tools defined";

        var sb = new StringBuilder();
        foreach (var tool in tools)
        {
            sb.AppendLine($"            \"{tool.Name}\" => Task.FromResult(\"TODO: implement {EscapeString(tool.Name)}\"),");
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task WriteFileAsync(
        string baseDir, string relativePath, string content, CancellationToken ct)
    {
        var fullPath = Path.Combine(baseDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(fullPath, content, ct);
    }

    private static string ToPascalCase(string snakeCase)
    {
        return string.Concat(
            snakeCase.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static string EscapeString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex ModuleIdRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9]{0,19}$")]
    private static partial Regex ToolPrefixRegex();
}
