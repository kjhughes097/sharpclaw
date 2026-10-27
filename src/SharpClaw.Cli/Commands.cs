using SharpClaw.Contracts;

namespace SharpClaw.Cli;

public static class Commands
{
    /// <param name="readSecret">Reads a password without echo (prompt in, value out); defaults to a line from <paramref name="input"/>.</param>
    public static async Task RunAsync(
        string[] args, ICliGateway gateway, TextWriter output, TextReader input, CancellationToken ct,
        Func<string, string?>? readSecret = null)
    {
        var parsed = ParsedArgs.Parse(args);
        if (parsed.Positionals.Count == 0)
            throw new CliException(Usage);
        readSecret ??= prompt =>
        {
            output.Write(prompt);
            return input.ReadLine();
        };

        var group = parsed.Positionals[0];
        switch (group)
        {
            case "users":
                await Users(parsed, gateway, output, input, readSecret);
                break;
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
                await Ask(parsed, gateway, output, input, ct);
                break;
            case "chat":
                await Chat(parsed, gateway, output, input, ct);
                break;
            case "approvals":
                await Approvals(parsed, gateway, output);
                break;
            default:
                throw new CliException(Usage);
        }
    }

    private static async Task Users(ParsedArgs a, ICliGateway gw, TextWriter o, TextReader input, Func<string, string?> readSecret)
    {
        var verb = a.Positionals.ElementAtOrDefault(1) ?? throw new CliException(Usage);
        if (verb == "list")
        {
            var list = await gw.ListUsersAsync();
            await o.WriteLineAsync($"{"NAME",-32} {"DISABLED",-9} {"CREATED",-20}");
            foreach (var u in list.Users)
                await o.WriteLineAsync($"{u.Name,-32} {(u.Disabled ? "yes" : "no"),-9} {u.CreatedAt:u}");
            return;
        }

        var name = a.Positionals.ElementAtOrDefault(2) ?? throw new CliException("user name is required.");
        switch (verb)
        {
            case "create":
                await gw.CreateUserAsync(name, ReadNewPassword(a, input, readSecret));
                await o.WriteLineAsync($"User {name} created.");
                break;
            case "set-password":
                await gw.SetUserPasswordAsync(name, ReadNewPassword(a, input, readSecret));
                await o.WriteLineAsync($"Password set for {name}; their sessions were signed out.");
                break;
            case "disable" or "enable":
                var user = await gw.SetUserDisabledAsync(name, verb == "disable");
                await o.WriteLineAsync($"User {user.Name} {(user.Disabled ? "disabled; their sessions were signed out" : "enabled")}.");
                break;
            default:
                throw new CliException(Usage);
        }
    }

    /// <summary><c>--password-stdin</c> reads one line (scripts); otherwise prompt twice without echo.</summary>
    private static string ReadNewPassword(ParsedArgs a, TextReader input, Func<string, string?> readSecret)
    {
        if (a.Has("password-stdin"))
            return input.ReadLine() ?? throw new CliException("no password on stdin.");
        var first = readSecret("Password: ") ?? "";
        var second = readSecret("Confirm password: ") ?? "";
        if (first != second)
            throw new CliException("Passwords do not match.");
        return first;
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
                var created = await gw.CreateAgentAsync(name, model, budget, ContextWindow(a));
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
                var updated = await gw.PatchAgentAsync(name, a.Get("model"), a.GetDouble("daily-budget"), a.GetBool("enabled"),
                    ContextWindow(a));
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
        if (a.Positionals.ElementAtOrDefault(1) == "tools")
        {
            await AuditTools(a, gw, o);
            return;
        }

        var result = await gw.AuditAsync(a.Get("agent"), a.GetInt("limit"));
        await o.WriteLineAsync($"{"CREATED",-22} {"MODEL",-24} {"STATUS",-10} {"PROMPT",-7} {"COMPL",-7} {"COST",-10} {"MS",-7}");
        foreach (var c in result.Calls)
            await o.WriteLineAsync(
                $"{c.CreatedAt:u} {c.Model,-24} {c.Status,-10} {c.PromptTokens,-7} {c.CompletionTokens,-7} {c.CostUsd,-10:0.######} {c.LatencyMs,-7}");
    }

    private static async Task AuditTools(ParsedArgs a, ICliGateway gw, TextWriter o)
    {
        var result = await gw.AuditToolCallsAsync(a.Get("agent"), a.GetInt("limit"));
        await o.WriteLineAsync($"{"CREATED",-22} {"TOOL",-18} {"STATUS",-18} {"DECISION",-9} {"RISK",-9} {"TAINT",-6} {"MS",-6}");
        foreach (var c in result.Calls)
            await o.WriteLineAsync(
                $"{c.CreatedAt:u} {c.ToolName,-18} {c.Status,-18} {c.Decision ?? "-",-9} {c.Risk ?? "-",-9} {(c.Tainted ? "yes" : "no"),-6} {c.DurationMs?.ToString() ?? "-",-6}");
    }

    private static async Task Ask(ParsedArgs a, ICliGateway gw, TextWriter o, TextReader input, CancellationToken ct)
    {
        var agent = a.Positionals.ElementAtOrDefault(1) ?? throw new CliException("agent is required.");
        var text = a.Positionals.ElementAtOrDefault(2) ?? throw new CliException("message text is required.");
        var thread = await ResolveThread(a, gw, agent);
        await ChatRunner.RunOnceAsync(gw, thread, text, o, input, ct, unattended: a.Has("unattended"));
    }

    private static async Task Approvals(ParsedArgs a, ICliGateway gw, TextWriter o)
    {
        var verb = a.Positionals.ElementAtOrDefault(1) ?? throw new CliException(Usage);
        switch (verb)
        {
            case "list":
            {
                var list = await gw.ListApprovalsAsync(a.Get("status"), a.Get("agent"));
                await o.WriteLineAsync($"{"ID",-38} {"AGENT",-14} {"TOOL",-18} {"RISK",-9} {"STATUS",-9} {"EXPIRES",-20} SUMMARY");
                foreach (var ap in list.Approvals)
                    await o.WriteLineAsync(
                        $"{ap.Id,-38} {ap.Agent,-14} {ap.ToolName,-18} {ap.Risk,-9} {ap.Status,-9} {ap.ExpiresAt:u} {ap.Summary}");
                break;
            }
            case "approve" or "deny":
            {
                var raw = a.Positionals.ElementAtOrDefault(2) ?? throw new CliException("approval id is required.");
                if (!Guid.TryParse(raw, out var id)) throw new CliException("approval id must be a GUID.");
                var decided = await gw.DecideApprovalAsync(id,
                    verb == "approve" ? ApprovalStatuses.Approved : ApprovalStatuses.Denied);
                await o.WriteLineAsync($"Approval {decided.Id}: {decided.Status}.");
                break;
            }
            default:
                throw new CliException(Usage);
        }
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
            await ChatRunner.RunOnceAsync(gw, thread, line, o, input, ct);
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

    private static int? ContextWindow(ParsedArgs a) =>
        a.Has("context-window")
            ? a.GetInt("context-window") ?? throw new CliException("--context-window must be a whole number of tokens.")
            : null;

    private static void PrintAgent(TextWriter o, AgentDto a)
    {
        o.WriteLine($"id:          {a.Id}");
        o.WriteLine($"name:        {a.Name}");
        o.WriteLine($"model:       {a.Model}");
        o.WriteLine($"dailyBudget: {a.DailyBudgetUsd:0.####} USD");
        o.WriteLine($"enabled:     {a.Enabled}");
        o.WriteLine($"context:     {(a.ContextWindowTokens is { } w ? $"{w} tokens" : "gateway default")}");
        o.WriteLine($"todaySpend:  {a.TodaySpendUsd:0.####} USD");
        o.WriteLine($"remaining:   {a.RemainingUsd:0.####} USD");
    }

    public const string Usage = """
        Usage:
          sharpclaw agents create <name> --model <id> --daily-budget <usd> [--context-window <tokens>]
          sharpclaw agents list
          sharpclaw agents show <name>
          sharpclaw agents set <name> [--model <id>] [--daily-budget <usd>] [--enabled true|false] [--context-window <tokens>]
          sharpclaw tokens issue <agent> [--expires <utc>]
          sharpclaw tokens list <agent>
          sharpclaw tokens revoke <agent> <tokenId>
          sharpclaw chat <agent> [--thread <id>]
          sharpclaw ask <agent> "<text>" [--unattended]
          sharpclaw approvals list [--status pending] [--agent <name>]
          sharpclaw approvals approve <id>
          sharpclaw approvals deny <id>
          sharpclaw users create <name> [--password-stdin]
          sharpclaw users list
          sharpclaw users set-password <name> [--password-stdin]
          sharpclaw users disable <name>
          sharpclaw users enable <name>
          sharpclaw audit [--agent <name>] [--limit N]
          sharpclaw audit tools [--agent <name>] [--limit N]
        """;
}
