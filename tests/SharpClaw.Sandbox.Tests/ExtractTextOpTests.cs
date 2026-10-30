using System.Text;
using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Sandbox.Infrastructure;
using SharpClaw.Sandbox.Ops;

namespace SharpClaw.Sandbox.Tests;

/// <summary><c>extract_text</c> (spec section 7.3): fixed converters, workspace paths in and out.</summary>
public class ExtractTextOpTests : IDisposable
{
    private readonly TempWorkspace _ws = new();
    private readonly ExtractTextOp _op;

    public ExtractTextOpTests() => _op = new ExtractTextOp(new WorkspaceRoot(_ws.Path), ChildEnvironment.Build());

    public void Dispose() => _ws.Dispose();

    private async Task<JsonElement> Run(object args) =>
        JsonDocument.Parse((await _op.RunAsync(JsonSerializer.SerializeToElement(args), default)).Json).RootElement;

    /// <summary>A minimal one-page PDF showing <paramref name="text"/>.</summary>
    public static byte[] Pdf(string text)
    {
        var stream = $"BT /F1 24 Tf 50 80 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 144] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            pdf.Append($"{o:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    [Fact]
    public async Task Html_becomes_markdown()
    {
        File.WriteAllText(_ws.File("page.html"), "<h1>Title</h1><p>Body <b>bold</b></p>");

        var r = await Run(new { path = "page.html", to = "out/page.md" });

        Assert.True(r.GetProperty("ok").GetBoolean());
        var md = File.ReadAllText(_ws.File("out/page.md"));
        Assert.Contains("# Title", md);
        Assert.Contains("**bold**", md);
    }

    [Fact]
    public async Task Pdf_becomes_text()
    {
        File.WriteAllBytes(_ws.File("paper.pdf"), Pdf("Hello PDF"));

        var r = await Run(new { path = "paper.pdf", to = "paper.txt" });

        Assert.True(r.GetProperty("ok").GetBoolean(), r.GetRawText());
        Assert.Contains("Hello PDF", File.ReadAllText(_ws.File("paper.txt")));
    }

    [Fact]
    public async Task A_broken_pdf_fails_cleanly()
    {
        File.WriteAllText(_ws.File("bad.pdf"), "not a pdf");

        Assert.Equal("conversion_failed", (await Run(new { path = "bad.pdf", to = "bad.txt" })).GetProperty("error").GetString());
        Assert.False(File.Exists(_ws.File("bad.txt")));
    }

    [Theory]
    [InlineData("notes.txt", "out.md", "unsupported_type")]
    [InlineData("missing.pdf", "out.txt", "not_found")]
    [InlineData("../x.pdf", "out.txt", "invalid_path")]
    [InlineData("page.html", "/etc/out.txt", "invalid_path")]
    [InlineData("page.html", "page.html", "invalid_path")]
    [InlineData("page.html", "existing.md", "exists")]
    public async Task Refusals(string path, string to, string error)
    {
        File.WriteAllText(_ws.File("notes.txt"), "x");
        File.WriteAllText(_ws.File("page.html"), "<p>x</p>");
        File.WriteAllText(_ws.File("existing.md"), "keep");

        Assert.Equal(error, (await Run(new { path, to })).GetProperty("error").GetString());
        Assert.Equal("keep", File.ReadAllText(_ws.File("existing.md")));
    }

    [Fact]
    public async Task A_linked_input_is_refused()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"secret-{Guid.NewGuid():N}.html");
        File.WriteAllText(outside, "<p>outside secret</p>");
        File.CreateSymbolicLink(_ws.File("link.html"), outside);
        try
        {
            Assert.Equal("invalid_path", (await Run(new { path = "link.html", to = "out.md" })).GetProperty("error").GetString());
            Assert.False(File.Exists(_ws.File("out.md")));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
