namespace SharpClaw.Core;

/// <summary>
/// Controls whether a tool call is allowed to proceed.
/// </summary>
public enum ToolPermission
{
    /// <summary>Tool calls proceed without user interaction.</summary>
    AutoApprove,

    /// <summary>The user is prompted before each call.</summary>
    Ask,

    /// <summary>Tool calls are silently blocked.</summary>
    Deny,
}
