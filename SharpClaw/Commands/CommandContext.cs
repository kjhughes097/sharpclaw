namespace SharpClaw.Commands;

public sealed record CommandContext(
    string ChannelKey,
    string RawText,
    string? CurrentAgentId
);

public sealed record CommandResult(
    bool Handled,
    string? ResponseText = null,
    string? SwitchedToAgent = null
);
