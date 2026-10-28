using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Tests;

public class TelemetryAndAbortTests
{
    [Fact]
    public async Task Scripted_call_emits_metrics_with_expected_labels()
    {
        var measurements = new ConcurrentBag<(string Name, double Value, Dictionary<string, string?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == GatewayMetrics.MeterName)
                l.EnableMeasurementEvents(inst);
        };
        void Record<T>(Instrument inst, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags) where T : struct
        {
            var dict = new Dictionary<string, string?>();
            foreach (var t in tags) dict[t.Key] = t.Value?.ToString();
            measurements.Add((inst.Name, Convert.ToDouble(value), dict));
        }
        listener.SetMeasurementEventCallback<long>((i, v, t, s) => Record(i, v, t));
        listener.SetMeasurementEventCallback<double>((i, v, t, s) => Record(i, v, t));
        listener.Start();

        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello");
        var corr = (await fx.ClaimAsync(token))!.Id;

        var resp = await fx.ChatAsync(token, corr, thread);
        await resp.Content.ReadAsStringAsync();

        var calls = measurements.Where(m => m.Name == "sharpclaw.llm.calls").ToList();
        Assert.Contains(calls, m =>
            m.Value == 1 &&
            m.Tags.GetValueOrDefault("status") == LlmCallStatus.Ok &&
            m.Tags.GetValueOrDefault("agent") == agent.Name &&
            m.Tags.GetValueOrDefault("model") == agent.Model);

        Assert.Contains(measurements, m => m.Name == "sharpclaw.llm.cost_usd" && m.Value > 0);
        Assert.Contains(measurements, m =>
            m.Name == "sharpclaw.llm.tokens" && m.Tags.GetValueOrDefault("type") == "completion");
    }

    [Fact]
    public async Task Logs_contain_no_secrets_or_content()
    {
        var logs = new ConcurrentQueue<string>();
        await using var fx = await GatewayFixture.CreateAsync(configure: builder =>
        {
            builder.Logging.AddProvider(new CapturingLoggerProvider(logs));
        });
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "SECRET_CONTENT_12345");
        var corr = (await fx.ClaimAsync(token))!.Id;
        var resp = await fx.ChatAsync(token, corr, thread, userText: "SECRET_CONTENT_12345");
        await resp.Content.ReadAsStringAsync();

        var all = string.Join("\n", logs);
        Assert.DoesNotContain("SECRET_CONTENT_12345", all);
        Assert.DoesNotContain(token, all);
        Assert.DoesNotContain(GatewayFixture.AdminKey, all);
    }

    [Fact]
    public async Task Gauges_report_pending_approvals_unreported_tool_calls_and_subscribers()
    {
        var script = ChatTestHelpers.WriteScript("""
            { "rules": [ { "match": "go", "toolCalls": [ { "name": "workspace_list", "arguments": {} } ] } ] }
            """);
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: new Dictionary<string, string?>
        {
            ["Fake:ScriptPath"] = script,
            ["Approvals:TimeoutSeconds"] = "3600",
        });
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "go");
        var corr = (await fx.ClaimAsync(token))!.Id;
        await (await fx.ChatAsync(token, corr, thread, "go")).Content.ReadAsStringAsync();
        using (var c = fx.AgentClient(token))
            (await c.PostAsJsonAsync("/agent/approvals", new CreateApprovalRequest(corr, thread, "call_x", "workspace_delete",
                "delete a.md", RiskLevels.High, "deletes"), SharpClawJson.Options)).EnsureSuccessStatusCode();
        var (streamClient, stream) = await fx.OpenStreamAsync($"/api/threads/{thread}/stream");
        using var __ = streamClient;
        await using var ___ = stream;
        fx.Time.Advance(TimeSpan.FromMinutes(6));

        var observed = new ConcurrentBag<(string Name, int Value, Dictionary<string, string?> Tags)>();
        var meter = fx.App.Services.GetRequiredService<GatewayMetrics>().Meter;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            // Only this fixture's meter: other tests' gateways may be shutting down concurrently.
            if (ReferenceEquals(inst.Meter, meter))
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<int>((inst, v, tags, _) =>
        {
            var dict = new Dictionary<string, string?>();
            foreach (var t in tags) dict[t.Key] = t.Value?.ToString();
            observed.Add((inst.Name, v, dict));
        });
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Contains(observed, m => m.Name == "sharpclaw.approvals.pending" && m.Value == 1 && m.Tags["agent"] == agent.Name);
        Assert.Contains(observed, m => m.Name == "sharpclaw.tool.unreported" && m.Value == 1 && m.Tags["agent"] == agent.Name);
        Assert.Contains(observed, m => m.Name == "sharpclaw.sse.subscribers" && m.Value == 1 && m.Tags["scope"] == "thread");
        Assert.Contains(observed, m => m.Name == "sharpclaw.sse.subscribers" && m.Value == 0 && m.Tags["scope"] == "global");
    }

    [Fact]
    public async Task Unreachable_otlp_endpoint_does_not_break_requests()
    {
        const string key = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";
        var previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "http://127.0.0.1:1/v1/metrics");
        try
        {
            await using var fx = await GatewayFixture.CreateAsync();
            using var admin = fx.Admin();
            var resp = await admin.GetAsync("/api/agents");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Fact]
    public async Task Client_abort_records_cancelled_call()
    {
        var script = ChatTestHelpers.WriteScript("""
        { "rules": [ { "match": ".*",
          "response": "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789",
          "chunkSize": 1, "delayMs": 30 } ] }
        """);
        await using var fx = await GatewayFixture.CreateAsync(
            extraConfig: new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello");
        var corr = (await fx.ClaimAsync(token))!.Id;

        using var c = fx.AgentClient(token);
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { stream = true, messages = new[] { new { role = "user", content = "hi" } } }),
        };
        req.Headers.TryAddWithoutValidation("X-Correlation-Id", corr.ToString());
        req.Headers.TryAddWithoutValidation("X-Thread-Id", thread.ToString());

        var resp = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var stream = await resp.Content.ReadAsStreamAsync();
        var buffer = new byte[16];
        _ = await stream.ReadAsync(buffer); // read a little, then abort the connection
        resp.Dispose();

        // The gateway records a cancelled call shortly after detecting the abort.
        LlmCall? call = null;
        await TestHelpers.EventuallyAsync(async () =>
        {
            using var db = fx.NewDb();
            call = await db.LlmCalls.AsNoTracking().FirstOrDefaultAsync();
            return call is not null;
        }, because: "the cancelled llm_calls row");
        Assert.NotNull(call);
        Assert.Equal(LlmCallStatus.Cancelled, call!.Status);
        Assert.False(call.UsageKnown);
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _logs;
        public CapturingLoggerProvider(ConcurrentQueue<string> logs) => _logs = logs;
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_logs, categoryName);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _logs;
            private readonly string _category;
            public CapturingLogger(ConcurrentQueue<string> logs, string category)
            {
                _logs = logs;
                _category = category;
            }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _logs.Enqueue($"{_category}: {formatter(state, exception)} {exception}");
        }
    }
}
