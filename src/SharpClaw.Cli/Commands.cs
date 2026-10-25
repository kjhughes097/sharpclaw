using SharpClaw.Contracts;

namespace SharpClaw.Cli;

public static class Commands
{
    public static async Task RunAsync(
        string[] args, ICliGateway gateway, TextWriter output, TextReader input, CancellationToken ct)
    {
        var parsed = ParsedArgs.Parse(args);
        if (parsed.Positionals.Count == 0)
            throw new CliException(Usage);

        var group = parsed.Positionals[0];
        switch (group)
        {
            case "agents":
                await Agents(parsed, gateway, output);
                break;
            case "tokens":
                await Tokens(parsed, gateway, output);
                break;
            case "audit":
                await Audit(parsed, gateway, output);
                break;
            case "ask":
                await Ask(parsed, gateway, output, ct);
                break;
            case "chat":
                await Chat(parsed, gateway, output, input, ct);
                break;
            default:
                throw new CliException(Usage);
        }
    }

    private static async Task Agents(ParsedArgs a, ICliGateway gw, TextWriter o)
    {
        var verb = a.Positionals.ElementAtOrDefault(1) ?? throw new CliException(Usage);
        switch (verb)
        {
            case "create":
            {
                var name = a.Positionals.ElementAtOrDefault(2) ?? throw new CliException("agent name is required.");
                var model = a.Get("model") ?? throw new CliException("--model is required.");
                var budget = a.GetDouble("daily-budget") ?? throw new CliException("--daily-budget is required.");
                var created = await gw.CreateAgentAsync(name, model, budget);
                PrintAgent(o, created.Agent);
                await o.WriteLineAsync();
                await o.WriteLineAsync($"Token (shown once, store it now): {created.Token.Value}");
                break;
            }
            case "list":
            {
                var list = await gw.ListAgentsAsync();
                await o.WriteLineAsync($"{"NAME",-20} {"MODEL",-28} {"ENABLED",-8} {"SPEND",-10} {"REMAINING",-10}");
                foreach (var ag in list.Agents)
                    await o.WriteLineAsync(
                        $"{ag.Name,-20} {ag.Model,-28} {ag.Enabled,-8} {ag.TodaySpendUsd,-10:0.####} {ag.RemainingUsd,-10:0.####}");
                break;
            }
            case "show":
            {
                var name = a.Positionals.ElementAtOrDefault(2) ?? throw new CliException("agent name is required.");
                PrintAgent(o, await gw.GetAgentAsync(name));
                break;
            }
            case "set":
            {
                var name = a.Positionals.ElementAtOrDefault(2) ?? throw new CliException("agent name is required.");
                var updated = await gw.PatchAgentAsync(name, a.Get("model"), a.GetDouble("daily-budget"), a.GetBool("enabled"));
                PrintAgent(o, updated);
                break;
            }
            default:
                throw new CliException(Usage);
        }
    }

    private static async Task Tokens(ParsedArgs a, ICliGateway gw, TextWriter o)
    {
        var verb = a.Positionals.ElementAtOrDefault(1) ?? throw new CliException(Usage);
        var agent = a.Positionals.ElementAtOrDefault(2) ?? throw new CliException("agent is required.");
        switch (verb)
        {
            case "issue":
            {
                DateTime? expires = null;
                if (a.Get("expires") is { } e)
                {
                    if (!DateTime.TryParse(e, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                        throw new CliException("--expires must be a UTC timestamp.");
                    expires = dt;
                }
                var created = await gw.IssueTokenAsync(agent, expires);
                await o.WriteLineAsync($"Token (shown once, store it now): {created.Token.Value}");
                break;
            }
            case "list":
            {
                var list = await gw.ListTokensAsync(agent);
                await o.WriteLineAsync($"{"ID",-38} {"PREFIX",-10} {"EXPIRES",-22} {"REVOKED",-22}");
                foreach (var t in list.Tokens)
                    await o.WriteLineAsync($"{t.Id,-38} {t.Prefix,-10} {t.ExpiresAt?.ToString("u") ?? "-",-22} {t.RevokedAt?.ToString("u") ?? "-",-22}");
                break;
            }
            case "revoke":
            {
                var tokenId = a.Positionals.ElementAtOrDefault(3) ?? throw new CliException("token id is required.");
                await gw.RevokeTokenAsync(agent, tokenId);
                await o.WriteLineAsync("Revoked.");
                break;
            }
            default:
                throw new CliException(Usage);
        }
    }

    private static async Task Audit(ParsedArgs a, ICliGateway gw, TextWriter o)
    {
        var result = await gw.AuditAsync(a.Get("agent"), a.GetInt("limit"));
        await o.WriteLineAsync($"{"CREATED",-22} {"MODEL",-24} {"STATUS",-10} {"PROMPT",-7} {"COMPL",-7} {"COST",-10} {"MS",-7}");
        foreach (var c in result.Calls)
            await o.WriteLineAsync(
                $"{c.CreatedAt:u} {c.Model,-24} {c.Status,-10} {c.PromptTokens,-7} {c.CompletionTokens,-7} {c.CostUsd,-10:0.######} {c.LatencyMs,-7}");
    }

    private static async Task Ask(ParsedArgs a, ICliGateway gw, TextWriter o, CancellationToken ct)
    {
        var agent = a.Positionals.ElementAtOrDefault(1) ?? throw new CliException("agent is required.");
        var text = a.Positionals.ElementAtOrDefault(2) ?? throw new CliException("message text is required.");
        var thread = await ResolveThread(a, gw, agent);
        await ChatRunner.RunOnceAsync(gw, thread, text, o, ct);
    }

    private static async Task Chat(ParsedArgs a, ICliGateway gw, TextWriter o, TextReader input, CancellationToken ct)
    {
        var agent = a.Positionals.ElementAtOrDefault(1) ?? throw new CliException("agent is required.");
        var thread = await ResolveThread(a, gw, agent);
        await o.WriteLineAsync($"Chatting with {agent} (thread {thread}). Type /exit to quit.");

        while (!ct.IsCancellationRequested)
        {
            await o.WriteAsync("> ");
            await o.FlushAsync(ct);
            var line = await input.ReadLineAsync(ct);
            if (line is null || line.Trim() == "/exit") break;
            if (line.Length == 0) continue;
            await ChatRunner.RunOnceAsync(gw, thread, line, o, ct);
        }
    }

    private static async Task<Guid> ResolveThread(ParsedArgs a, ICliGateway gw, string agent)
    {
        if (a.Get("thread") is { } t)
        {
            if (!Guid.TryParse(t, out var id)) throw new CliException("--thread must be a GUID.");
            return id;
        }
        var thread = await gw.CreateThreadAsync(agent, null);
        return thread.Id;
    }

    private static void PrintAgent(TextWriter o, AgentDto a)
    {
        o.WriteLine($"id:          {a.Id}");
        o.WriteLine($"name:        {a.Name}");
        o.WriteLine($"model:       {a.Model}");
        o.WriteLine($"dailyBudget: {a.DailyBudgetUsd:0.####} USD");
        o.WriteLine($"enabled:     {a.Enabled}");
        o.WriteLine($"todaySpend:  {a.TodaySpendUsd:0.####} USD");
        o.WriteLine($"remaining:   {a.RemainingUsd:0.####} USD");
    }

    public const string Usage = """
        Usage:
          sharpclaw agents create <name> --model <id> --daily-budget <usd>
          sharpclaw agents list
          sharpclaw agents show <name>
          sharpclaw agents set <name> [--model <id>] [--daily-budget <usd>] [--enabled true|false]
          sharpclaw tokens issue <agent> [--expires <utc>]
          sharpclaw tokens list <agent>
          sharpclaw tokens revoke <agent> <tokenId>
          sharpclaw chat <agent> [--thread <id>]
          sharpclaw ask <agent> "<text>"
          sharpclaw audit [--agent <name>] [--limit N]
        """;
}
