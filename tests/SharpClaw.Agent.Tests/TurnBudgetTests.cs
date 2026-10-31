using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tests;

/// <summary><see cref="TurnBudget"/>: a turn's tool results are stubbed, oldest first, until the request fits.</summary>
public class TurnBudgetTests
{
    private static ChatMessage Result(string id, string content) => new(Roles.Tool, content, ToolCallId: id);
    private static ChatMessage Call(string id) => new(Roles.Assistant, null, new[] { new ChatToolCall(id, "t", "{}") });
    private static string Big(int chars, string? savedPath = null) =>
        JsonSerializer.Serialize(new { ok = true, content = new string('x', chars), savedPath });

    private static List<ChatMessage> Turn(params string[] results)
    {
        var messages = new List<ChatMessage> { new(Roles.System, "soul"), new(Roles.User, new string('u', 400)) };
        for (var i = 0; i < results.Length; i++)
        {
            messages.Add(Call($"c{i}"));
            messages.Add(Result($"c{i}", results[i]));
        }
        return messages;
    }

    [Fact]
    public void A_request_that_fits_is_left_alone()
    {
        var messages = Turn(Big(1000));
        var before = messages.ToList();

        var (compacted, fits) = TurnBudget.Fit(messages, turnStart: 2, schemaChars: 0, windowTokens: 32000);

        Assert.Equal((0, true), (compacted, fits));
        Assert.Equal(before, messages);
    }

    [Fact]
    public void The_oldest_results_are_stubbed_first_and_only_as_many_as_needed()
    {
        // Three results of ~10k tokens each in a 25k window: dropping the first is enough.
        var messages = Turn(Big(40_000, ".sandbox/fetch/a.md"), Big(40_000), Big(40_000));
        var window = TurnBudget.Estimate(messages, 0) - 9000 + TurnBudget.ReplyReserveTokens;

        var (compacted, fits) = TurnBudget.Fit(messages, turnStart: 2, schemaChars: 0, windowTokens: window);

        Assert.Equal((1, true), (compacted, fits));
        var stub = JsonDocument.Parse(messages[3].Content!).RootElement;
        Assert.True(stub.GetProperty("compacted").GetBoolean());
        Assert.Contains(".sandbox/fetch/a.md", stub.GetProperty("note").GetString());
        Assert.Equal("c0", messages[3].ToolCallId);
        Assert.Equal(40_000, JsonDocument.Parse(messages[5].Content!).RootElement.GetProperty("content").GetString()!.Length);
        Assert.True(TurnBudget.Estimate(messages, 0) <= window - TurnBudget.ReplyReserveTokens);
    }

    [Fact]
    public void Messages_before_the_turn_are_never_touched()
    {
        var messages = Turn(Big(40_000));
        messages.Insert(2, Result("old", Big(40_000))); // a history row before the turn starts

        var (_, fits) = TurnBudget.Fit(messages, turnStart: 3, schemaChars: 0, windowTokens: 2000 + 100);

        Assert.False(fits);
        Assert.Equal(Big(40_000), messages[2].Content);
        Assert.StartsWith("""{"ok":true,"compacted":true""", messages[4].Content);
    }

    [Fact]
    public void A_stub_points_at_a_run_log_and_is_never_replaced_again()
    {
        var log = JsonSerializer.Serialize(new { ok = true, stdout = new string('o', 20_000), logPath = ".sandbox/logs/run-1.log" });
        var messages = Turn(log);

        TurnBudget.Fit(messages, 2, 0, windowTokens: 2500);
        var stub = messages[3].Content!;
        TurnBudget.Fit(messages, 2, 0, windowTokens: 2500);

        Assert.Contains(".sandbox/logs/run-1.log", stub);
        Assert.Equal(stub, messages[3].Content);
    }

    [Fact]
    public void Without_a_saved_copy_the_stub_says_to_run_the_tool_again()
    {
        Assert.Contains("Run the tool again", TurnBudget.Stub(Big(100)));
        Assert.Contains("Run the tool again", TurnBudget.Stub("not json"));
    }
}
