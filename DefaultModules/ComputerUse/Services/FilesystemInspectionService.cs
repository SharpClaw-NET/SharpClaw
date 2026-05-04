using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Modules.ComputerUse.Services;

/// <summary>
/// Strictly read-only filesystem inspection. The agent can list directories,
/// stat paths, search by glob, and read text files — but cannot write,
/// delete, move, execute, or otherwise mutate anything.
/// </summary>
public sealed class FilesystemInspectionService
{
    // Hard caps so an agent can't accidentally DoS the host.
    private const int DefaultMaxEntries = 500;
    private const int MaxEntriesUpperBound = 5_000;
    private const int DefaultMaxDepth = 3;
    private const int MaxDepthUpperBound = 16;
    private const int DefaultReadMaxBytes = 256 * 1024;          //  256 KiB
    private const int ReadMaxBytesUpperBound = 4 * 1024 * 1024;  //    4 MiB
    private const int DefaultFindMaxResults = 200;
    private const int FindMaxResultsUpperBound = 2_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ListDirectory(string path, bool recursive, int? maxEntries, int? maxDepth, string? includeGlob)
    {
        var root = (DirectoryInfo)ResolveExisting(path, mustBeDirectory: true);
        var cap = Clamp(maxEntries ?? DefaultMaxEntries, 1, MaxEntriesUpperBound);
        var depthCap = Clamp(maxDepth ?? DefaultMaxDepth, 0, MaxDepthUpperBound);

        var matcher = BuildGlobMatcher(includeGlob);
        var entries = new List<DirEntry>();
        var truncated = false;

        try
        {
            EnumerateInto(root, root, recursive, depthCap, 0, matcher, entries, cap, ref truncated);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Access denied while enumerating '{root.FullName}': {ex.Message}");
        }

        return JsonSerializer.Serialize(new
        {
            root = root.FullName,
            recursive,
            max_entries = cap,
            max_depth = depthCap,
            include_glob = includeGlob,
            count = entries.Count,
            truncated,
            entries,
        }, Json);
    }

    public string Stat(string path)
    {
        var resolved = Resolve(path);

        if (Directory.Exists(resolved))
        {
            var di = new DirectoryInfo(resolved);
            return JsonSerializer.Serialize(new
            {
                path = di.FullName,
                exists = true,
                kind = "directory",
                created_utc = di.CreationTimeUtc,
                modified_utc = di.LastWriteTimeUtc,
                attributes = di.Attributes.ToString(),
            }, Json);
        }

        if (File.Exists(resolved))
        {
            var fi = new FileInfo(resolved);
            return JsonSerializer.Serialize(new
            {
                path = fi.FullName,
                exists = true,
                kind = "file",
                size_bytes = fi.Length,
                created_utc = fi.CreationTimeUtc,
                modified_utc = fi.LastWriteTimeUtc,
                attributes = fi.Attributes.ToString(),
                read_only = fi.IsReadOnly,
                extension = fi.Extension,
            }, Json);
        }

        return JsonSerializer.Serialize(new
        {
            path = resolved,
            exists = false,
        }, Json);
    }

    public string FindFiles(string rootPath, string glob, bool recursive, int? maxResults)
    {
        if (string.IsNullOrWhiteSpace(glob))
            throw new InvalidOperationException("find_files requires a non-empty 'glob'.");

        var root = (DirectoryInfo)ResolveExisting(rootPath, mustBeDirectory: true);
        var cap = Clamp(maxResults ?? DefaultFindMaxResults, 1, FindMaxResultsUpperBound);

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var matches = new List<object>(cap);
        var truncated = false;

        IEnumerable<string> enumeration;
        try
        {
            enumeration = Directory.EnumerateFiles(root.FullName, glob, new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                MaxRecursionDepth = MaxDepthUpperBound,
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Access denied while searching '{root.FullName}': {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid search pattern '{glob}': {ex.Message}");
        }

        foreach (var file in enumeration)
        {
            if (matches.Count >= cap)
            {
                truncated = true;
                break;
            }

            FileInfo fi;
            try { fi = new FileInfo(file); }
            catch { continue; }

            matches.Add(new
            {
                path = fi.FullName,
                relative = Path.GetRelativePath(root.FullName, fi.FullName),
                size_bytes = SafeLength(fi),
                modified_utc = SafeWriteUtc(fi),
            });
        }

        return JsonSerializer.Serialize(new
        {
            root = root.FullName,
            glob,
            recursive,
            max_results = cap,
            count = matches.Count,
            truncated,
            matches,
        }, Json);
    }

    public string ReadFileText(string path, int? maxBytes, int? startLine, int? endLine)
    {
        var file = (FileInfo)ResolveExisting(path, mustBeDirectory: false);

        var cap = Clamp(maxBytes ?? DefaultReadMaxBytes, 1, ReadMaxBytesUpperBound);

        if (file.Length == 0)
            return string.Empty;

        // Read up to the cap, then optionally slice by line range.
        var toRead = (int)Math.Min(file.Length, cap);
        var truncated = file.Length > toRead;

        using var stream = file.OpenRead();
        var buffer = new byte[toRead];
        var read = 0;
        while (read < toRead)
        {
            var n = stream.Read(buffer, read, toRead - read);
            if (n <= 0) break;
            read += n;
        }

        var text = DecodeText(buffer.AsSpan(0, read));

        if (startLine.HasValue || endLine.HasValue)
        {
            var lines = text.Split('\n');
            var from = Math.Max(1, startLine ?? 1);
            var to = Math.Min(lines.Length, endLine ?? lines.Length);
            if (from > lines.Length || to < from)
                return string.Empty;

            var sb = new StringBuilder();
            for (var i = from - 1; i < to; i++)
            {
                if (i > from - 1) sb.Append('\n');
                sb.Append(lines[i]);
            }
            text = sb.ToString();
        }

        if (truncated)
            text += $"\n\n[... truncated: file is {file.Length} bytes, read {read} bytes ...]";

        return text;
    }

    // ── helpers ──────────────────────────────────────────────────

    private static FileSystemInfo ResolveExisting(string path, bool mustBeDirectory)
    {
        var resolved = Resolve(path);
        if (mustBeDirectory)
        {
            if (Directory.Exists(resolved))
                return new DirectoryInfo(resolved);
            throw new InvalidOperationException(
                File.Exists(resolved)
                    ? $"Path '{resolved}' is a file, not a directory."
                    : $"Directory '{resolved}' does not exist.");
        }

        if (File.Exists(resolved))
            return new FileInfo(resolved);
        throw new InvalidOperationException(
            Directory.Exists(resolved)
                ? $"Path '{resolved}' is a directory, not a file."
                : $"File '{resolved}' does not exist.");
    }

    private static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("'path' is required.");

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            return Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"Invalid path '{path}': {ex.Message}");
        }
    }

    private static void EnumerateInto(
        DirectoryInfo root, DirectoryInfo current,
        bool recursive, int depthCap, int depth,
        Func<string, bool>? matcher,
        List<DirEntry> entries, int cap, ref bool truncated)
    {
        if (entries.Count >= cap)
        {
            truncated = true;
            return;
        }

        IEnumerable<FileSystemInfo> children;
        try { children = current.EnumerateFileSystemInfos(); }
        catch (UnauthorizedAccessException) { return; }
        catch (DirectoryNotFoundException) { return; }

        foreach (var child in children)
        {
            if (entries.Count >= cap)
            {
                truncated = true;
                return;
            }

            var rel = Path.GetRelativePath(root.FullName, child.FullName);
            var isDir = child is DirectoryInfo;
            var passes = matcher is null || matcher(rel) || matcher(child.Name);

            if (passes)
            {
                long? size = null;
                if (child is FileInfo fi)
                    size = SafeLength(fi);

                entries.Add(new DirEntry(
                    Name: child.Name,
                    RelativePath: rel,
                    Kind: isDir ? "directory" : "file",
                    SizeBytes: size,
                    ModifiedUtc: SafeWriteUtc(child)));
            }

            if (recursive && isDir && depth < depthCap)
            {
                EnumerateInto(root, (DirectoryInfo)child, recursive, depthCap, depth + 1,
                    matcher, entries, cap, ref truncated);
            }
        }
    }

    private static long? SafeLength(FileInfo fi)
    {
        try { return fi.Length; }
        catch { return null; }
    }

    private static DateTime? SafeWriteUtc(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch { return null; }
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    private static Func<string, bool>? BuildGlobMatcher(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob)) return null;

        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(glob)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/\\\\]*")
            .Replace("\\?", ".") + "$";

        var regex = new System.Text.RegularExpressions.Regex(
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        return s => regex.IsMatch(s.Replace('\\', '/'));
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        // Detect a leading BOM; otherwise default to UTF-8 with replacement.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        return utf8.GetString(bytes);
    }

    private sealed record DirEntry(
        string Name,
        string RelativePath,
        string Kind,
        long? SizeBytes,
        DateTime? ModifiedUtc);
}
