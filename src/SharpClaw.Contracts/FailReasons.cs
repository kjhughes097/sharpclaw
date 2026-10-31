namespace SharpClaw.Contracts;

/// <summary>
/// Why an agent failed a user turn (<c>POST /agent/messages/{id}/fail</c>, <c>messages.fail_reason</c>),
/// and the plain text shown for each in the SSE <c>error</c> event and the CLI.
/// The web UI has its own (localisable) table in <c>ui/src/api/errors.ts</c>.
/// </summary>
public static class FailReasons
{
    public const string AgentError = "agent_error";
    public const string ToolLoopLimit = "tool_loop_limit";

    /// <summary>The turn's own tool calls and results outgrew the window (not the user's message).</summary>
    public const string TurnTooLarge = "turn_too_large";

    /// <summary>Readable text for a fail code; unknown codes get a generic sentence that names the code.</summary>
    public static string Describe(string code) => code switch
    {
        ErrorCodes.BudgetExceeded => "The agent's daily budget is used up. It resets at midnight UTC.",
        ErrorCodes.LoopLimitExceeded => "The agent made too many model calls for this message.",
        ToolLoopLimit => "The agent kept calling tools without finishing, so it stopped.",
        ErrorCodes.ContextTooLarge => "That is too large for the model's context window. Shorten it or start a new chat.",
        TurnTooLarge => "The agent's tool results outgrew the model's context window. Ask for a smaller step, or give the agent a model with a larger window.",
        ErrorCodes.ProviderError => "The model provider returned an error. Try again.",
        ErrorCodes.ProviderTimeout => "The model provider timed out. Try again.",
        AgentError => "The agent hit an internal error.",
        _ => $"The agent could not complete this message ({code}).",
    };
}
