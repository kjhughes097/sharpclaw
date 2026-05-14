---
sidebar_position: 11
---

# Auditing

SharpClaw maintains a per-agent audit log that records all requests and responses as markdown files.

## AuditService

```csharp
public sealed class AuditService(IOptions<SharpClawOptions> options, ILogger<AuditService> logger)
```

### Log Location

Each agent's audit log is at `{WorkspacePath}/{agentName}/audit.md`.

### Entry Format

```markdown
### 2026-05-05 14:30:00
- **Type**: Request
- **Content**: What's the status of the deployment?
```

### Entry Types

```csharp
public enum AuditEntryType
{
    Request,
    Response
}
```

### Thread Safety

File writes are serialised with a `Lock` to prevent interleaving from concurrent requests.

### Truncation

Content is truncated to 500 characters in audit entries to keep the log manageable.

## Integration

`AgentInvoker` calls `AuditService.LogAsync()` for every request and response:

```csharp
await auditService.LogAsync(session.AgentId, AuditEntryType.Request, prompt, ct);
// ... after getting response ...
await auditService.LogAsync(session.AgentId, AuditEntryType.Response, responseText, ct);
```

## Configuration

Auditing is automatically enabled when `WorkspacePath` is configured. If the path is empty, `LogAsync` returns immediately without writing.
