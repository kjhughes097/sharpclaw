using System.Runtime.CompilerServices;
using SharpClaw.Cli;
using SharpClaw.Contracts;

namespace SharpClaw.Cli.Tests;

public partial class CliTests
{
    [Fact]
    public void ArgumentParser_splits_positionals_and_options()
    {
        var parsed = ParsedArgs.Parse(new[]
        {
            "agents", "create", "assistant", "--model", "a/b", "--daily-budget", "1.5", "--enabled",
        });

        Assert.Equal(new[] { "agents", "create", "assistant" }, parsed.Positionals);
        Assert.Equal("a/b", parsed.Get("model"));
        Assert.Equal(1.5, parsed.GetDouble("daily-budget"));
        Assert.True(parsed.GetBool("enabled"));
    }

    [Fact]
    public async Task Create_agent_prints_token_once()
    {
        var gw = new FakeGateway();
        var output = new StringWriter();
        await Commands.RunAsync(
            new[] { "agents", "create", "assistant", "--model", "m", "--daily-budget", "1" },
            gw, output, TextReader.Null, CancellationToken.None);

        var text = output.ToString();
        Assert.Contains("sc_fake-token", text);
        Assert.Contains("shown once", text);
    }

    [Fact]
    public async Task Audit_tools_prints_the_tool_call_table()
    {
        var gw = new FakeGateway();
        gw.ToolCalls.Add(new ToolCallDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "call_1", "workspace_delete", "{}", "denied", "denied", "high", true, Guid.NewGuid(), "denied_by_user", 12,
            TestClock.Now, TestClock.Now));
        var output = new StringWriter();

        await Commands.RunAsync(new[] { "audit", "tools", "--agent", "assistant", "--limit", "5" }, gw, output, TextReader.Null, CancellationToken.None);

        Assert.Equal(("assistant", 5), gw.LastToolAuditQuery);
        var text = output.ToString();
        Assert.Contains("TOOL", text);
        Assert.Contains("workspace_delete", text);
        Assert.Contains("high", text);
        Assert.Contains("yes", text);
    }

    // ---- reconnect (pre-3.3 §8.6) ----

    [Theory]
    [InlineData("create")]
    [InlineData("set")]
    public async Task Context_window_flag_is_passed_through(string verb)
    {
        var gw = new FakeGateway();
        var args = verb == "create"
            ? new[] { "agents", "create", "a", "--model", "m", "--daily-budget", "1", "--context-window", "128000" }
            : new[] { "agents", "set", "a", "--context-window", "128000" };

        await Commands.RunAsync(args, gw, new StringWriter(), TextReader.Null, CancellationToken.None);

        Assert.Equal(128000, gw.LastContextWindow);
    }

    // ---- users (pre-3.3 §4.7) ----

    [Fact]
    public async Task Users_create_prompts_twice_without_echo()
    {
        var gw = new FakeGateway();
        var prompts = new List<string>();
        var output = new StringWriter();

        await Commands.RunAsync(new[] { "users", "create", "kevin" }, gw, output, TextReader.Null, CancellationToken.None,
            p => { prompts.Add(p); return "correct horse battery"; });

        Assert.Equal(new[] { "Password: ", "Confirm password: " }, prompts);
        Assert.Equal(("kevin", "correct horse battery"), Assert.Single(gw.CreatedUsers));
        Assert.DoesNotContain("correct horse battery", output.ToString());
    }

    [Fact]
    public async Task Users_create_rejects_mismatched_confirmation()
    {
        var gw = new FakeGateway();
        var answers = new Queue<string>(new[] { "one password here", "another password" });

        await Assert.ThrowsAsync<CliException>(() => Commands.RunAsync(new[] { "users", "create", "kevin" }, gw,
            new StringWriter(), TextReader.Null, CancellationToken.None, _ => answers.Dequeue()));
        Assert.Empty(gw.CreatedUsers);
    }

    [Fact]
    public async Task Users_set_password_reads_stdin_and_enable_disable_and_list_work()
    {
        var gw = new FakeGateway();
        var output = new StringWriter();

        await Commands.RunAsync(new[] { "users", "set-password", "kevin", "--password-stdin" }, gw, output,
            new StringReader("from stdin 123\n"), CancellationToken.None);
        await Commands.RunAsync(new[] { "users", "disable", "kevin" }, gw, output, TextReader.Null, CancellationToken.None);
        await Commands.RunAsync(new[] { "users", "enable", "kevin" }, gw, output, TextReader.Null, CancellationToken.None);
        await Commands.RunAsync(new[] { "users", "list" }, gw, output, TextReader.Null, CancellationToken.None);

        Assert.Equal(("kevin", "from stdin 123"), Assert.Single(gw.PasswordsSet));
        Assert.Equal(new[] { ("kevin", true), ("kevin", false) }, gw.DisabledChanges);
        Assert.Contains("kevin", output.ToString());
    }

    // ---- approvals queue (pre-3.3 §9.6) ----

    [Fact]
    public async Task Approvals_list_passes_filters_and_prints_the_queue()
    {
        var gw = new FakeGateway();
        gw.Approvals.Add(new ApprovalDetailDto(Guid.NewGuid(), "assistant", Guid.NewGuid(), Guid.NewGuid(), "workspace_delete",
            "delete old.md", "high", "deletes a file", "pending", TestClock.Now, TestClock.Now.AddMinutes(5), null, null));
        var output = new StringWriter();

        await Commands.RunAsync(new[] { "approvals", "list", "--status", "pending", "--agent", "assistant" },
            gw, output, TextReader.Null, CancellationToken.None);

        Assert.Equal(("pending", "assistant"), gw.LastApprovalQuery);
        Assert.Contains("workspace_delete", output.ToString());
        Assert.Contains("delete old.md", output.ToString());
    }

    [Theory]
    [InlineData("approve", "approved")]
    [InlineData("deny", "denied")]
    public async Task Approvals_approve_and_deny_post_the_decision(string verb, string expected)
    {
        var gw = new FakeGateway();
        var id = Guid.NewGuid();
        var output = new StringWriter();

        await Commands.RunAsync(new[] { "approvals", verb, id.ToString() }, gw, output, TextReader.Null, CancellationToken.None);

        Assert.Equal((id, expected), Assert.Single(gw.Decisions));
        Assert.Contains(expected, output.ToString());
    }

    [Fact]
    public async Task Ask_unattended_posts_the_flag()
    {
        var gw = new FakeGateway();
        await Commands.RunAsync(new[] { "ask", "assistant", "hi", "--unattended" }, gw, new StringWriter(), TextReader.Null, CancellationToken.None);
        Assert.True(gw.PostedUnattended);
    }
}

/// <summary>A fixed instant for fake DTO timestamps; tests never read the real clock.</summary>
internal static class TestClock
{
    public static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
}
