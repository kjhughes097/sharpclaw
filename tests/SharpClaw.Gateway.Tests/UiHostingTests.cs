using System.Net;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Tests;

/// <summary>Static UI hosting, SPA fallback, caching and security headers (3.3 spec section 6).</summary>
public sealed class UiHostingTests : IAsyncLifetime
{
    private const string IndexHtml =
        "<!doctype html><html><head><script type=\"module\" src=\"/assets/app-abc123.js\"></script></head>" +
        "<body><div id=\"root\"></div></body></html>";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sharpclaw-ui-{Guid.NewGuid():N}");
    private GatewayFixture _fx = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), IndexHtml);
        await File.WriteAllTextAsync(Path.Combine(_root, "assets", "app-abc123.js"), "console.log('ui');" + new string(' ', 2000));
        await File.WriteAllTextAsync(Path.Combine(_root, "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        _fx = await GatewayFixture.CreateAsync(extraConfig: new Dictionary<string, string?> { ["Ui:RootPath"] = _root });
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void AssertSecurityHeaders(HttpResponseMessage resp)
    {
        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", resp.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", resp.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("camera=(), microphone=(), geolocation=(), payment=()", resp.Headers.GetValues("Permissions-Policy").Single());
        Assert.Equal("same-origin", resp.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
        Assert.Equal("same-origin", resp.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.False(resp.Headers.Contains("Strict-Transport-Security"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/chats")]
    [InlineData("/chats/0199f0a0-0000-7000-8000-000000000001")]
    [InlineData("/agents/assistant")]
    public async Task Client_routes_get_the_index_with_csp_and_no_cache(string path)
    {
        using var http = _fx.ClientRaw();
        var resp = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType!.MediaType);
        Assert.Equal(IndexHtml, await resp.Content.ReadAsStringAsync());
        Assert.Equal("no-cache", resp.Headers.CacheControl!.ToString());
        var csp = resp.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Equal(UiHosting.ContentSecurityPolicy, csp);
        Assert.Equal(
            "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
            "connect-src 'self'; manifest-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'", csp);
        Assert.DoesNotContain("upgrade-insecure-requests", csp);
        AssertSecurityHeaders(resp);

        var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }

    [Fact]
    public async Task Hashed_assets_are_immutable_and_compressed_and_missing_assets_404()
    {
        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = new Uri(_fx.ClientBaseUrl),
        };
        var request = new HttpRequestMessage(HttpMethod.Get, "/assets/app-abc123.js");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        var asset = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl!.ToString());
        Assert.Equal("gzip", Assert.Single(asset.Content.Headers.ContentEncoding));
        AssertSecurityHeaders(asset);
        Assert.False(asset.Headers.Contains("Content-Security-Policy"));

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/favicon.svg")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/assets/missing-123.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/missing.png")).StatusCode);
    }

    [Fact]
    public async Task Api_paths_are_never_the_index_and_are_no_store()
    {
        using var http = _fx.ClientRaw();
        var unknownApi = await http.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
        Assert.Equal("no-store", unknownApi.Headers.CacheControl!.ToString());

        var unauthorized = await http.GetAsync("/api/threads");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("no-store", unauthorized.Headers.CacheControl!.ToString());
        AssertSecurityHeaders(unauthorized);
        Assert.False(unauthorized.Headers.Contains("Content-Security-Policy"));

        using var admin = _fx.Admin();
        var system = await admin.GetAsync("/api/system");
        Assert.Equal("no-store", system.Headers.CacheControl!.ToString());
        Assert.Equal("no-store", (await admin.GetAsync("/openapi/v1.json")).Headers.CacheControl!.ToString());
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync("/openapi/other")).StatusCode);

        var health = await http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("application/json", health.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/chats")]
    [InlineData("/index.html")]
    [InlineData("/assets/app-abc123.js")]
    public async Task The_agent_listener_never_serves_the_ui(string path)
    {
        using var http = _fx.AgentRaw();
        var resp = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.False(resp.Headers.Contains("Content-Security-Policy"));
    }
}
