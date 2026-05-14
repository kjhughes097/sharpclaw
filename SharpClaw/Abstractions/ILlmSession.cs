namespace SharpClaw.Abstractions;

public interface ILlmSession : IAsyncDisposable
{
    string SessionId { get; }
}
