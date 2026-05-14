using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpClaw.Abstractions;
using SharpClaw.Models;
using SharpClaw.Scheduling;

namespace SharpClaw.Execution;

public sealed class ToolAIFunctionAdapter : AIFunction
{
    private readonly ITool _tool;
    private readonly JsonElement _jsonSchema;
    private readonly SchedulingContextAccessor? _schedulingContextAccessor;

    public ToolAIFunctionAdapter(ITool tool, SchedulingContextAccessor? schedulingContextAccessor = null)
    {
        _tool = tool;
        _schedulingContextAccessor = schedulingContextAccessor;

        var schema = new
        {
            type = "object",
            properties = tool.Parameters.ToDictionary(
                p => p.Name,
                p => (object)new { type = p.Type, description = p.Description }),
            required = tool.Parameters.Where(p => p.Required).Select(p => p.Name).ToArray()
        };
        _jsonSchema = JsonSerializer.SerializeToElement(schema);
    }

    public override string Name => _tool.Name;
    public override string Description => _tool.Description;
    public override JsonElement JsonSchema => _jsonSchema;

    /// <summary>
    /// Captures the current scheduling context so it can be restored when
    /// the Copilot SDK invokes tool callbacks (which may not flow ExecutionContext).
    /// Call this just before sending a message to the SDK.
    /// </summary>
    public void CaptureSchedulingContext()
    {
        _capturedSchedulingContext = _schedulingContextAccessor?.Current;
    }

    private SchedulingContext? _capturedSchedulingContext;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Restore the scheduling context in case the SDK didn't flow ExecutionContext
        if (_schedulingContextAccessor is not null && _capturedSchedulingContext is not null)
            _schedulingContextAccessor.Current = _capturedSchedulingContext;

        var context = new ToolCallContext(_tool.Name, arguments);
        return await _tool.ExecuteAsync(context, cancellationToken);
    }
}
