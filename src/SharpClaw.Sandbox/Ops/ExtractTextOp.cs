using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;
using SharpClaw.Sandbox.Infrastructure;

namespace SharpClaw.Sandbox.Ops;

/// <summary>
/// <c>extract_text</c> (spec section 7.3): HTML → Markdown with <c>pandoc --sandbox -f html -t gfm</c>,
/// PDF → text with <c>pdftotext -layout</c>. Fixed argv only, never a model-supplied flag. The runner
/// reads the input and writes the output itself (both without following links), so the converters
/// only ever see stdin and stdout, never a workspace path.
/// </summary>
public sealed class ExtractTextOp(WorkspaceRoot workspace, IReadOnlyDictionary<string, string> childEnv) : IRunnerOp
{
    public const long MaxInputBytes = 50L * 1024 * 1024;
    public const int MaxOutputBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public string Name => BrokerTools.ExtractText;

    public async Task<OpResult> RunAsync(JsonElement args, CancellationToken ct)
    {
        var (input, inputError) = workspace.Resolve(OpArgs.String(args, "path"));
        if (input is null)
            return OpResult.Error("invalid_path", $"path: {inputError}");
        var (output, outputError) = workspace.Resolve(OpArgs.String(args, "to"));
        if (output is null)
            return OpResult.Error("invalid_path", $"to: {outputError}");
        if (Converter(input.Relative) is not { } converter)
            return OpResult.Error("unsupported_type", "path must be an .html, .htm or .pdf file.");
        if (input.FullPath == output.FullPath)
            return OpResult.Error("invalid_path", "to must differ from path.");
        if (!File.Exists(input.FullPath))
            return OpResult.Error("not_found", $"No file at '{input.Relative}'.");
        if (new FileInfo(input.FullPath).Length > MaxInputBytes)
            return OpResult.Error("too_large", $"The file is larger than {MaxInputBytes / (1024 * 1024)} MB.");
        if (Directory.Exists(output.FullPath) || File.Exists(output.FullPath))
            return OpResult.Error("exists", $"'{output.Relative}' already exists; choose another name.");

        try
        {
            var content = await NoFollow.ReadAllBytesAsync(workspace.Path, input.Relative, ct);
            var result = await ProcessRunner.RunAsync(new ProcessSpec(
                converter.File, converter.Args, Path.GetTempPath(), Timeout, MaxOutputBytes, childEnv, Stdin: content), ct);
            if (result.TimedOut)
                return OpResult.Error("timeout", $"Extraction took longer than {Timeout.TotalSeconds:0} s.");
            if (result.ExitCode != 0)
                return OpResult.Error("conversion_failed", $"{converter.File} could not read the file (exit {result.ExitCode}).");

            await NoFollow.WriteAtomicAsync(workspace.Path, output.Relative, result.Stdout, ct);
            return OpResult.Success(new { path = input.Relative, to = output.Relative, bytes = result.Stdout.Length, truncated = result.Truncated });
        }
        catch (WorkspacePathException ex)
        {
            return OpResult.Error("invalid_path", ex.Message);
        }
    }

    private static (string File, string[] Args)? Converter(string relative) =>
        Path.GetExtension(relative).ToLowerInvariant() switch
        {
            ".html" or ".htm" => ("pandoc", ["--sandbox", "-f", "html", "-t", "gfm", "--wrap=none"]),
            ".pdf" => ("pdftotext", ["-layout", "-", "-"]),
            _ => null,
        };
}
