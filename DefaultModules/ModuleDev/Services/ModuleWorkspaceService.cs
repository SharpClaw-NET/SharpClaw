using System.Text;

using SharpClaw.Application.Services;
using SharpClaw.Utils.Security;

namespace SharpClaw.Modules.ModuleDev.Services;

/// <summary>
/// File I/O scoped to the <c>external-modules/</c> subtree.
/// Validates all paths against traversal and extension blocklists.
/// </summary>
internal sealed class ModuleWorkspaceService
{
    private static readonly HashSet<string> AllowedExtensions = [".cs", ".json"];

    private readonly string _externalModulesDir;

    public ModuleWorkspaceService()
    {
        _externalModulesDir = ModuleService.ResolveExternalModulesDir();
    }

    /// <summary>
    /// Resolves and validates a module workspace root directory.
    /// </summary>
    public string ResolveModuleDir(string moduleId)
    {
        ValidateModuleId(moduleId);

        var moduleDir = Path.GetFullPath(Path.Combine(_externalModulesDir, moduleId));
        PathGuard.EnsureContainedIn(moduleDir, _externalModulesDir);

        return moduleDir;
    }

    /// <summary>
    /// Resolves a file path inside a module workspace.
    /// Rejects traversal, absolute paths, null bytes, and reserved names.
    /// </summary>
    public string ResolveFilePath(string moduleId, string relativePath)
    {
        ValidateModuleId(moduleId);
        ValidateRelativePath(relativePath);

        var moduleDir = ResolveModuleDir(moduleId);
        var fullPath = Path.GetFullPath(Path.Combine(moduleDir, relativePath));

        if (!fullPath.StartsWith(moduleDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(moduleDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path '{relativePath}' escapes the module workspace.");
        }

        return fullPath;
    }

    /// <summary>
    /// Writes a file to the module workspace. Creates intermediate directories.
    /// </summary>
    public async Task<(string Path, long BytesWritten)> WriteFileAsync(
        string moduleId, string relativePath, string content, CancellationToken ct = default)
    {
        var fullPath = ResolveFilePath(moduleId, relativePath);
        ValidateExtension(fullPath);
        PathGuard.EnsureContainedIn(fullPath, _externalModulesDir);

        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        var bytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(fullPath, bytes, ct);

        return (fullPath, bytes.Length);
    }

    /// <summary>
    /// Reads a file from the module workspace, optionally truncated.
    /// </summary>
    public async Task<string> ReadFileAsync(
        string moduleId, string relativePath, int maxLines = 500, CancellationToken ct = default)
    {
        var fullPath = ResolveFilePath(moduleId, relativePath);
        PathGuard.EnsureContainedIn(fullPath, _externalModulesDir);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}", fullPath);

        var lines = await File.ReadAllLinesAsync(fullPath, ct);

        if (lines.Length <= maxLines)
            return string.Join(Environment.NewLine, lines);

        var truncated = lines.Take(maxLines);
        return string.Join(Environment.NewLine, truncated) +
               $"{Environment.NewLine}... (truncated, {lines.Length - maxLines} lines omitted)";
    }

    /// <summary>
    /// Lists the file tree of a module workspace, optionally filtered by glob.
    /// </summary>
    public IReadOnlyList<string> ListFiles(string moduleId, string? includePattern = null)
    {
        var moduleDir = PathGuard.EnsureContainedIn(ResolveModuleDir(moduleId), _externalModulesDir);

        if (!Directory.Exists(moduleDir))
            return [];

        var pattern = includePattern ?? "*";
        var searchOption = pattern.Contains("**") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // Normalize glob: strip leading **/ for Directory.EnumerateFiles
        var filePattern = pattern
            .Replace("**/", "")
            .Replace("**\\", "");

        if (string.IsNullOrWhiteSpace(filePattern) || filePattern == "**")
            filePattern = "*";

        return Directory.EnumerateFiles(moduleDir, filePattern, searchOption)
            .Select(f => Path.GetRelativePath(moduleDir, f).Replace('\\', '/'))
            .Order()
            .ToList();
    }

    /// <summary>
    /// Gets the root path for external modules.
    /// </summary>
    public string ExternalModulesDir => _externalModulesDir;

    // ── Validation ────────────────────────────────────────────────

    private static void ValidateModuleId(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);

        if (!System.Text.RegularExpressions.Regex.IsMatch(moduleId, @"^[a-z][a-z0-9_]{0,39}$"))
            throw new ArgumentException(
                $"Invalid module ID '{moduleId}'. Must match ^[a-z][a-z0-9_]{{0,39}}$.", nameof(moduleId));
    }

    private static void ValidateRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path cannot be empty.", nameof(relativePath));

        if (relativePath.Contains('\0'))
            throw new ArgumentException("Path contains null bytes.", nameof(relativePath));

        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException(
                $"Absolute paths are not allowed: '{relativePath}'.", nameof(relativePath));

        if (relativePath.Contains(".."))
            throw new ArgumentException(
                $"Path traversal (..) is not allowed: '{relativePath}'.", nameof(relativePath));

        // Block reserved Windows device names
        var fileName = Path.GetFileNameWithoutExtension(relativePath).ToUpperInvariant();
        ReadOnlySpan<string> reserved = ["CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

        foreach (var name in reserved)
        {
            if (fileName == name)
                throw new ArgumentException(
                    $"Reserved Windows device name: '{relativePath}'.", nameof(relativePath));
        }
    }

    private static void ValidateExtension(string fullPath)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            throw new InvalidOperationException(
                $"Cannot write files with extension '{ext}'. Only .cs and .json files are allowed.");
    }
}
