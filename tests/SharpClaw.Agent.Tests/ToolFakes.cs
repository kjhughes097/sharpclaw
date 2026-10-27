using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpClaw.Agent;
using SharpClaw.Agent.Tools;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tests;

/// <summary>Test doubles for the tool framework: a scriptable tool and a recording gateway.</summary>
internal sealed class FakeTool : IAgentTool
{
    public FakeTool(string name, ToolAssessment assessment, bool untrusted = false)
    {
        Name = name;
        Assessment = assessment;
        ReturnsUntrustedContent = untrusted;
    }

    public string Name { get; }
    public string Description => "fake";
    public string ParametersJson => """{"type":"object"}""";
    public bool ReturnsUntrustedContent { get; }
    public ToolAssessment Assessment { get; set; }
    public Func<JsonElement, ToolResult> Execute { get; set; } = _ => ToolResult.Success(new { done = true });
    public int Executions { get; private set; }

    public ToolAssessment Assess(JsonElement args) => Assessment;
    public string Summarize(JsonElement args) => $"{Name} call";

    public Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
    {
        Executions++;
        return Task.FromResult(Execute(args));
    }
}

internal sealed class RecordingGateway : IGatewayClient
{
    /// <summary>The approval statuses returned by create then each poll, in order.</summary>
    public Queue<ApprovalDto> ApprovalReplies { get; } = new();
    public List<CreateApprovalRequest> ApprovalRequests { get; } = new();
    public List<ToolCallReportRequest> Reports { get; } = new();
    public List<IReadOnlyList<ChatMessage>> Classifications { get; } = new();
    public string? ClassifierReply { get; set; }
    public bool FailApprovals { get; set; }

    public static ApprovalDto Approval(string status, string? decidedBy = null) =>
        new(Guid.NewGuid(), status, decidedBy, DateTime.UtcNow.AddMinutes(5));

    public Task<ApprovalDto> RequestApprovalAsync(CreateApprovalRequest request, CancellationToken ct)
    {
        ApprovalRequests.Add(request);
        if (FailApprovals)
            throw new HttpRequestException("gateway down");
        return Task.FromResult(ApprovalReplies.Dequeue());
    }

    public Task<ApprovalDto> PollApprovalAsync(Guid approvalId, int waitSeconds, CancellationToken ct) =>
        Task.FromResult(ApprovalReplies.Dequeue());

    public Task ReportToolCallAsync(ToolCallReportRequest report, CancellationToken ct)
    {
        Reports.Add(report);
        return Task.CompletedTask;
    }

    public Task<string?> ClassifyRiskAsync(
        IReadOnlyList<ChatMessage> messages, Guid correlationId, Guid threadId, CancellationToken ct)
    {
        Classifications.Add(messages);
        return Task.FromResult(ClassifierReply);
    }

    public Task<InboxResult> PollInboxAsync(int waitSeconds, CancellationToken ct) => throw new NotSupportedException();
    public Task<ContextResult> GetContextAsync(Guid threadId, int reserveTokens, CancellationToken ct) => throw new NotSupportedException();
    public Task<ChatOutcome> StreamChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<IAgentTool> tools,
        Guid correlationId, Guid threadId, CancellationToken ct) => throw new NotSupportedException();
    public Task PostReplyAsync(Guid threadId, string content, Guid correlationId, IReadOnlyList<ToolExchangeDto> exchanges,
        CancellationToken ct) => throw new NotSupportedException();
    public Task FailAsync(Guid messageId, string reason, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
}
