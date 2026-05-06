namespace AIMemory.CodeIndex.Security;

public class FileFilter
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea",
        "vendor", "__pycache__", ".mypy_cache", ".pytest_cache",
        "dist", "build", "out", "target", "packages",
        ".next", ".nuxt", "coverage", ".terraform"
    };

    private static readonly HashSet<string> ExcludedFilePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".pem", ".key", ".pfx", ".p12", ".jks",
        ".exe", ".dll", ".so", ".dylib", ".bin",
        ".zip", ".tar", ".gz", ".rar", ".7z",
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".bmp", ".webp",
        ".mp3", ".mp4", ".wav", ".avi", ".mov",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx",
        ".woff", ".woff2", ".ttf", ".eot",
        ".lock", ".min.js", ".min.css",
        ".map"
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "credentials.json", "secrets.json", "appsettings.Development.json",
        ".env.local", ".env.production", ".env.development",
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml",
        ".DS_Store", "Thumbs.db"
    };

    public long MaxFileSizeBytes { get; set; } = 1_048_576; // 1MB default

    public bool ShouldIncludeDirectory(string dirName)
    {
        return !ExcludedDirectories.Contains(dirName);
    }

    public bool ShouldIncludeFile(string filePath, long fileSize)
    {
        if (fileSize > MaxFileSizeBytes)
            return false;

        var fileName = Path.GetFileName(filePath);
        if (ExcludedFileNames.Contains(fileName))
            return false;

        var ext = Path.GetExtension(filePath);
        if (ExcludedFilePatterns.Contains(ext))
            return false;

        // Check for .min.js, .min.css
        if (filePath.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public bool IsBinaryFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var buffer = new byte[512];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);

            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0) return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    public IEnumerable<string> EnumerateFiles(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);

        return EnumerateFilesRecursive(root, root);
    }

    /// <summary>
    /// True if any segment of <paramref name="filePath"/> below <paramref name="rootPath"/>
    /// is an excluded directory (.git, node_modules, bin, obj, etc.) or if the file itself
    /// fails <see cref="ShouldIncludeFile"/>. Used by callers that don't go through
    /// <see cref="EnumerateFiles"/> (e.g. the git tier in CodeAdapter, which gets paths
    /// directly from libgit2).
    /// </summary>
    public bool IsPathExcluded(string filePath, string rootPath)
    {
        var rel = Path.GetRelativePath(rootPath, filePath);
        if (rel == "." || string.IsNullOrEmpty(rel)) return false;

        foreach (var segment in rel.Split('/', '\\'))
        {
            if (string.IsNullOrEmpty(segment) || segment == ".") continue;
            if (!ShouldIncludeDirectory(segment)
                && Path.GetExtension(segment) == string.Empty)
            {
                // Treat as directory only if it has no extension (best-effort heuristic;
                // ".git" is the only common name with a dot that we exclude, and it's
                // explicitly in the excluded set anyway).
                return true;
            }
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!File.Exists(filePath)) return false;
        var info = new FileInfo(filePath);
        return !ShouldIncludeFile(filePath, info.Length);
    }

    private IEnumerable<string> EnumerateFilesRecursive(string directory, string rootPath)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            // Path traversal prevention: ensure we stay within root
            var fullPath = Path.GetFullPath(entry);
            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Directory.Exists(fullPath))
            {
                var dirName = Path.GetFileName(fullPath);
                if (ShouldIncludeDirectory(dirName))
                {
                    foreach (var file in EnumerateFilesRecursive(fullPath, rootPath))
                        yield return file;
                }
            }
            else if (File.Exists(fullPath))
            {
                var fileInfo = new FileInfo(fullPath);
                if (ShouldIncludeFile(fullPath, fileInfo.Length))
                    yield return fullPath;
            }
        }
    }

    public string GetRelativePath(string filePath, string rootPath)
    {
        return Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
    }
}
