namespace SharpClaw.Contracts;

/// <summary>What an attachment is (attachments spec section 1).</summary>
public static class AttachmentKinds
{
    public const string Image = "image";
    public const string Text = "text";
}

/// <summary>
/// Which files may be attached, decided by extension (attachments spec section 1). The web UI keeps
/// a copy of these lists for its file picker; the gateway is the authority.
/// </summary>
public static class AttachmentRules
{
    public const int DefaultMaxAttachments = 5;
    public const int DefaultMaxImageBytes = 5 * 1024 * 1024;
    public const int DefaultMaxTextBytes = 256 * 1024;
    public const int MaxFileNameChars = 255;
    public const string TextMediaType = "text/plain; charset=utf-8";

    /// <summary>Image extension → the media type its bytes must match.</summary>
    public static readonly IReadOnlyDictionary<string, string> ImageExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
        };

    public static readonly IReadOnlySet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".yaml", ".yml", ".xml", ".log",
        ".ini", ".toml", ".cfg", ".conf", ".html", ".htm", ".css", ".scss", ".js", ".mjs", ".cjs", ".ts",
        ".tsx", ".jsx", ".py", ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".java", ".kt",
        ".go", ".rs", ".rb", ".php", ".c", ".h", ".cpp", ".hpp", ".swift", ".sh", ".bash", ".ps1", ".sql",
        ".graphql", ".proto", ".diff", ".patch", ".tf", ".bicep",
    };

    /// <summary>Extension-less file names accepted as text.</summary>
    public static readonly IReadOnlySet<string> TextFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Makefile",
    };

    /// <summary>The kind for <paramref name="fileName"/>, or null when it can't be attached.</summary>
    public static string? KindOf(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ImageExtensions.ContainsKey(ext))
            return AttachmentKinds.Image;
        if (TextExtensions.Contains(ext) || (ext.Length == 0 && TextFileNames.Contains(fileName)))
            return AttachmentKinds.Text;
        return null;
    }
}
