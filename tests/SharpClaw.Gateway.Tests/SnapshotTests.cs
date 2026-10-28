using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// Committed contract snapshots (3.3 spec section 7): the OpenAPI document and one sample payload
/// per SSE event type. On mismatch, regenerate with <c>UPDATE_OPENAPI=1 dotnet test --filter
/// FullyQualifiedName~SnapshotTests</c>, review the diff, then run <c>npm run gen:api</c> in <c>ui/</c>.
/// </summary>
public class SnapshotTests
{
    private static bool Update => Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Stable key order so the snapshot only changes when the contract does.</summary>
    public static string Normalise(string json)
    {
        var node = Sort(JsonNode.Parse(json));
        return node!.ToJsonString(Indented).Replace("\r\n", "\n") + "\n";
    }

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(x => Sort(x?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    private static void Compare(string path, string actual, string what)
    {
        if (Update)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }
        Assert.True(File.Exists(path), $"{what} snapshot {path} is missing. Run with UPDATE_OPENAPI=1 to create it.");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(expected == actual,
            $"{what} changed. If intended, run `UPDATE_OPENAPI=1 dotnet test --filter FullyQualifiedName~SnapshotTests`, " +
            "review the diff, then `cd ui && npm run gen:api` and commit both.");
    }

    [Fact]
    public async Task OpenApi_document_matches_the_committed_snapshot()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        using var admin = fx.Admin();
        var json = await admin.GetStringAsync("/openapi/v1.json");
        var doc = JsonNode.Parse(json)!;

        // Client routes only.
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.All(paths, p => Assert.StartsWith("/api/", p));
        Assert.DoesNotContain(paths, p => p.StartsWith("/agent") || p.StartsWith("/v1"));
        Assert.NotNull(doc["components"]!["securitySchemes"]!["csrfToken"]);

        Compare(Path.Combine(ClientApiHelpers.RepoRoot(), "docs", "openapi", "sharpclaw-client.v1.json"),
            Normalise(json), "The OpenAPI document");
    }

    [Fact]
    public async Task OpenApi_requires_authentication_and_the_client_listener()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        using var anonymous = fx.ClientRaw();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/openapi/v1.json")).StatusCode);

        using var agentPort = fx.AgentRaw();
        agentPort.DefaultRequestHeaders.Authorization = new("Bearer", GatewayFixture.AdminKey);
        Assert.Equal(HttpStatusCode.NotFound, (await agentPort.GetAsync("/openapi/v1.json")).StatusCode);
    }

    /// <summary>One deterministic sample per event type, serialised exactly as the gateway sends it.</summary>
    public static IReadOnlyDictionary<string, object> SampleEvents()
    {
        var thread = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");
        var corr = Guid.Parse("0199f0a0-0000-7000-8000-000000000002");
        var reply = Guid.Parse("0199f0a0-0000-7000-8000-000000000003");
        var approval = Guid.Parse("0199f0a0-0000-7000-8000-000000000004");
        var agent = Guid.Parse("0199f0a0-0000-7000-8000-000000000005");
        var expires = new DateTime(2026, 10, 25, 12, 5, 0, DateTimeKind.Utc);
        return new Dictionary<string, object>
        {
            [SseEventNames.Ready] = new ReadyEvent(null),
            [SseEventNames.Reset] = new ResetEvent(ResetReasons.HistoryPruned),
            [SseEventNames.Delta] = new DeltaEvent(corr, "Hello", thread),
            [SseEventNames.Snapshot] = new SnapshotEvent(corr, "Hello wor", thread),
            [SseEventNames.MessageCreated] = new MessageCreatedEvent(corr, thread, corr),
            [SseEventNames.MessageCompleted] = new MessageCompletedEvent(corr, reply, "Hello world", thread),
            [SseEventNames.Error] = new ErrorEvent(corr, ErrorCodes.ProviderError, FailReasons.Describe(ErrorCodes.ProviderError), thread),
            [SseEventNames.ToolRequested] = new ToolRequestedEvent(corr, "call_1", "workspace_read", thread),
            [SseEventNames.ApprovalRequested] = new ApprovalRequestedEvent(approval, corr, "workspace_delete",
                "Delete notes/a.md", RiskLevels.High, "Deletes a file", expires, thread),
            [SseEventNames.ApprovalDecided] = new ApprovalDecidedEvent(approval, corr, ApprovalStatuses.Approved,
                ApprovalDecidedBy.User, thread, "kevin"),
            [SseEventNames.ThreadCreated] = new ThreadCreatedEvent(thread, agent),
            [SseEventNames.ThreadUpdated] = new ThreadUpdatedEvent(thread, "Trip plan", false),
        };
    }

    [Fact]
    public void Event_fixtures_match_the_committed_files()
    {
        var dir = Path.Combine(ClientApiHelpers.RepoRoot(), "docs", "api-fixtures", "events");
        var samples = SampleEvents();
        // Every event name the gateway knows has a fixture.
        var names = typeof(SseEventNames).GetFields().Select(f => (string)f.GetValue(null)!).ToList();
        Assert.Equal(names.OrderBy(n => n), samples.Keys.OrderBy(n => n));

        foreach (var (type, payload) in samples)
        {
            var json = Normalise(JsonSerializer.Serialize(payload, payload.GetType(), SharpClawJson.Options));
            // Every payload carries threadId (null only for the global stream's framing events).
            Assert.True(JsonNode.Parse(json)!.AsObject().ContainsKey("threadId"), $"{type} has no threadId");
            Compare(Path.Combine(dir, type + ".json"), json, $"The {type} event fixture");
        }
    }
}
