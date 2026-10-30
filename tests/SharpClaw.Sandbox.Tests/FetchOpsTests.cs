using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Sandbox.Infrastructure;
using SharpClaw.Sandbox.Ops;

namespace SharpClaw.Sandbox.Tests;

/// <summary><c>http_fetch</c> and <c>download_to_workspace</c> (spec section 7.1) against a loopback server.</summary>
public class FetchOpsTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private readonly TempWorkspace _ws = new();
    private readonly Fetcher _fetcher = new(new ProxiedOutboundHttp(null));

    public async Task InitializeAsync()
    {
        _server = await new TestServer(app =>
        {
            app.MapGet("/page", () => TestServer.Html("<html><body><h1>Title</h1><p>Hello <a href=\"/x\">link</a></p><script>evil()</script></body></html>"));
            app.MapGet("/json", () => Results.Text("""{"a":1}""", "application/json"));
            app.MapGet("/bin", () => Results.Bytes(new byte[] { 0, 1, 2, 3 }, "application/octet-stream"));
            app.MapGet("/big", () => Results.Text(new string('é', 100_000), "text/plain; charset=utf-8"));
            app.MapGet("/missing", () => Results.NotFound());
            app.MapGet("/redirect", () => Results.Redirect("/page"));
            app.MapGet("/loop/{n:int}", (int n) => Results.Redirect($"/loop/{n + 1}"));
            app.MapGet("/to-ftp", () => Results.Redirect("ftp://example.com/file"));
            app.MapGet("/to-creds", () => Results.Redirect("http://user:pw@127.0.0.1/"));
            app.MapGet("/file", () => Results.Bytes(Encoding.UTF8.GetBytes("downloaded"), "text/plain"));
            app.MapGet("/stream", async (HttpContext ctx) =>
            {
                // Chunked, no Content-Length: the cap must be enforced while streaming.
                ctx.Response.ContentType = "application/octet-stream";
                for (var i = 0; i < 64; i++)
                    await ctx.Response.Body.WriteAsync(new byte[1024]);
            });
        }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _ws.Dispose();
    }

    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);
    private static JsonElement Parse(OpResult r) => JsonDocument.Parse(r.Json).RootElement;

    private Task<OpResult> Fetch(object args) => new HttpFetchOp(_fetcher, ChildEnvironment.Build()).RunAsync(Args(args), default);
    private Task<OpResult> Download(object args, long max = DownloadOp.MaxBytes) =>
        new DownloadOp(_fetcher, new WorkspaceRoot(_ws.Path), max).RunAsync(Args(args), default);

    [Fact]
    public async Task Html_is_converted_to_markdown_by_default()
    {
        var r = Parse(await Fetch(new { url = _server.Url("/page") }));

        Assert.True(r.GetProperty("ok").GetBoolean());
        var content = r.GetProperty("content").GetString()!;
        Assert.Contains("# Title", content);
        Assert.Contains("[link](/x)", content);
        Assert.Equal("markdown", r.GetProperty("format").GetString());
        Assert.False(r.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Text_and_raw_formats()
    {
        var text = Parse(await Fetch(new { url = _server.Url("/page"), format = "text" })).GetProperty("content").GetString()!;
        var raw = Parse(await Fetch(new { url = _server.Url("/page"), format = "raw" })).GetProperty("content").GetString()!;

        Assert.Contains("Hello link", text);
        Assert.DoesNotContain("<p>", text);
        Assert.Contains("<script>evil()</script>", raw);
    }

    [Fact]
    public async Task Non_html_text_is_returned_as_is_and_binary_is_refused()
    {
        Assert.Equal("""{"a":1}""", Parse(await Fetch(new { url = _server.Url("/json") })).GetProperty("content").GetString());
        Assert.Equal("binary_content", Parse(await Fetch(new { url = _server.Url("/bin") })).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Results_are_capped_at_64_KB_on_a_character_boundary()
    {
        var r = Parse(await Fetch(new { url = _server.Url("/big") }));

        var content = r.GetProperty("content").GetString()!;
        Assert.True(r.GetProperty("truncated").GetBoolean());
        Assert.True(Encoding.UTF8.GetByteCount(content) <= HttpFetchOp.MaxResultBytes);
        Assert.All(content, c => Assert.Equal('é', c));
    }

    [Fact]
    public async Task Redirects_are_followed_and_checked_hop_by_hop()
    {
        var ok = Parse(await Fetch(new { url = _server.Url("/redirect") }));
        Assert.Equal(_server.Url("/page"), ok.GetProperty("url").GetString());

        Assert.Equal("too_many_redirects", Parse(await Fetch(new { url = _server.Url("/loop/0") })).GetProperty("error").GetString());
        Assert.Equal("bad_redirect", Parse(await Fetch(new { url = _server.Url("/to-ftp") })).GetProperty("error").GetString());
        Assert.Equal("bad_redirect", Parse(await Fetch(new { url = _server.Url("/to-creds") })).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:pw@example.com/")]
    [InlineData("not a url")]
    public async Task Bad_urls_are_refused(string url) =>
        Assert.Equal("invalid_arguments", Parse(await Fetch(new { url })).GetProperty("error").GetString());

    [Fact]
    public async Task Http_errors_are_reported_without_the_body()
    {
        var r = Parse(await Fetch(new { url = _server.Url("/missing") }));

        Assert.Equal(("http_error", 404), (r.GetProperty("error").GetString(), r.GetProperty("status").GetInt32()));
    }

    [Fact]
    public async Task Download_writes_into_the_workspace()
    {
        var r = Parse(await Download(new { url = _server.Url("/file"), path = "docs/a.txt" }));

        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(("docs/a.txt", 10, true), (r.GetProperty("path").GetString(), r.GetProperty("bytes").GetInt32(), r.GetProperty("created").GetBoolean()));
        Assert.Equal("downloaded", File.ReadAllText(_ws.File("docs/a.txt")));
    }

    [Fact]
    public async Task Download_refuses_to_overwrite_unless_asked()
    {
        File.WriteAllText(_ws.File("a.txt"), "old");

        Assert.Equal("exists", Parse(await Download(new { url = _server.Url("/file"), path = "a.txt" })).GetProperty("error").GetString());
        Assert.Equal("old", File.ReadAllText(_ws.File("a.txt")));
        Assert.True(Parse(await Download(new { url = _server.Url("/file"), path = "a.txt", overwrite = true })).GetProperty("ok").GetBoolean());
        Assert.Equal("downloaded", File.ReadAllText(_ws.File("a.txt")));
    }

    [Fact]
    public async Task Download_over_the_cap_leaves_nothing_behind()
    {
        var r = Parse(await Download(new { url = _server.Url("/stream"), path = "big.bin" }, max: 10_000));

        Assert.Equal("too_large", r.GetProperty("error").GetString());
        Assert.Empty(Directory.GetFileSystemEntries(_ws.Path));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("link/x.txt")]
    public async Task Download_applies_the_workspace_path_rules(string path)
    {
        var outside = Path.Combine(Path.GetTempPath(), $"out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(_ws.File("link"), outside);
        try
        {
            Assert.Equal("invalid_path", Parse(await Download(new { url = _server.Url("/file"), path })).GetProperty("error").GetString());
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
