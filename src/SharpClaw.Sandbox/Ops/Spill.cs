namespace SharpClaw.Sandbox.Ops;

/// <summary>
/// Large tool output goes to a file in the workspace instead of into the model's context: the model
/// gets a short preview and the file's path, and reads the rest in pages with <c>workspace_read</c>.
/// A whole page or build log inline would crowd out the conversation (and can exceed the model's
/// window on its own).
/// </summary>
internal static class Spill
{
    /// <summary>The most output a sandbox tool returns inline (about 4k tokens).</summary>
    public const int InlineBytes = 16 * 1024;

    /// <summary>A new, unique workspace path such as <c>.sandbox/fetch/fetch-20261031-142200-1a2b3c.md</c>.</summary>
    public static string NewPath(string folder, string prefix, string extension, TimeProvider time) =>
        $".sandbox/{folder}/{prefix}-{time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.{extension}";
}
