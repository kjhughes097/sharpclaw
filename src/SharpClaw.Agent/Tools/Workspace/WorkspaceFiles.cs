using System.Text;

namespace SharpClaw.Agent.Tools.Workspace;

/// <summary>
/// File helpers shared by the workspace tools (agent tools spec section 3): the sensitive-path rule,
/// atomic writes, binary detection and a recursive walk that never follows symbolic links.
/// </summary>
internal static class WorkspaceFiles
{
    /// <summary>A file whose first 8 KB contains a NUL byte is treated as binary.</summary>
    public const int BinaryProbeBytes = 8 * 1024;

    // Script and executable types (and dotfiles such as .bashrc): nothing here runs them, but
    // something else could later, so the rules don't score them confidently.
    private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sh", ".bash", ".ps1", ".bat", ".cmd", ".exe", ".dll", ".so", ".py", ".js",
    };

    public static bool IsSensitive(string relative) =>
        relative.Split('/').Any(segment => segment.StartsWith('.') && segment is not ("." or "..")) ||
        SensitiveExtensions.Contains(System.IO.Path.GetExtension(relative));

    /// <summary>Writes via a temporary file in the same directory and a rename, so a crash never leaves a partial file.</summary>
    public static async Task WriteAtomicAsync(string fullPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        var temp = System.IO.Path.Combine(dir, $".{System.IO.Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                await stream.WriteAsync(content, ct);
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static Task WriteAtomicAsync(string fullPath, string content, CancellationToken ct) =>
        WriteAtomicAsync(fullPath, new UTF8Encoding(false).GetBytes(content), ct);

    public static bool IsBinary(ReadOnlySpan<byte> start) =>
        start[..Math.Min(start.Length, BinaryProbeBytes)].Contains((byte)0);

    public static bool IsBinaryFile(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        var buffer = new byte[BinaryProbeBytes];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return IsBinary(buffer.AsSpan(0, read));
    }

    /// <summary>
    /// Every regular file under <paramref name="directory"/>, depth first in ordinal name order.
    /// Symbolic links (to files or directories) are skipped, never followed; unreadable directories
    /// are skipped. Lazy, so callers can stop at their caps.
    /// </summary>
    public static IEnumerable<FileInfo> Walk(string directory)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(directory));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            var subdirectories = new List<DirectoryInfo>();
            foreach (var entry in entries)
            {
                if (entry.LinkTarget is not null)
                    continue;
                if (entry is FileInfo file)
                    yield return file;
                else if (entry is DirectoryInfo sub)
                    subdirectories.Add(sub);
            }

            // Pushed in reverse so the first subdirectory is walked next.
            for (var i = subdirectories.Count - 1; i >= 0; i--)
                pending.Push(subdirectories[i]);
        }
    }

    /// <summary>The '/'-separated path of <paramref name="fullPath"/> relative to <paramref name="baseDir"/>.</summary>
    public static string Relative(string baseDir, string fullPath) =>
        System.IO.Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/');
}

/// <summary>
/// Case-insensitive glob matching over '/'-separated relative paths: <c>*</c> and <c>?</c> match
/// within one segment, a <c>**</c> segment matches any number of segments. Hand-written (no regex),
/// so a model-supplied pattern can't trigger catastrophic backtracking.
/// </summary>
internal static class Glob
{
    public const int MaxPatternLength = 200;

    /// <summary>Returns an error reason when the pattern can't be used, otherwise null.</summary>
    public static string? Validate(string? pattern, string argument)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return $"{argument} (a glob such as **/*.md) is required";
        if (pattern.Length > MaxPatternLength)
            return $"{argument} is longer than {MaxPatternLength} characters";
        var normalised = pattern.Replace('\\', '/');
        if (normalised.StartsWith('/') || System.IO.Path.IsPathRooted(normalised) || normalised.Contains('\0'))
            return $"{argument} must be relative, e.g. **/*.md";
        if (normalised.Split('/').Any(s => s == ".."))
            return $"{argument} must not contain '..'";
        return null;
    }

    public static bool HasDirectory(string pattern) => pattern.Replace('\\', '/').Trim('/').Contains('/');

    public static bool IsMatch(string pattern, string relativePath)
    {
        var p = Segments(pattern);
        var s = Segments(relativePath);

        // matches[i, j]: pattern segments from i match path segments from j.
        var matches = new bool[p.Length + 1, s.Length + 1];
        matches[p.Length, s.Length] = true;
        for (var i = p.Length - 1; i >= 0; i--)
        {
            for (var j = s.Length; j >= 0; j--)
            {
                matches[i, j] = p[i] == "**"
                    ? matches[i + 1, j] || (j < s.Length && matches[i, j + 1])
                    : j < s.Length && SegmentMatches(p[i], s[j]) && matches[i + 1, j + 1];
            }
        }
        return matches[0, 0];
    }

    private static string[] Segments(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();

    /// <summary>Wildcard match of one segment: linear, with a single backtrack point for the last '*'.</summary>
    private static bool SegmentMatches(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])) && pattern[p] != '*')
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
