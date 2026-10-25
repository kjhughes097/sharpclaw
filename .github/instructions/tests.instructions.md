---
description: "Use when writing or fixing SharpClaw tests: xUnit, GatewayFixture, FakeProvider scripts, FakeTimeProvider, SSE/streaming assertions, agent loop and CLI fakes."
applyTo: "tests/**"
---
# SharpClaw Test Guidelines

- **Gateway tests** use `GatewayFixture.CreateAsync(...)` ([GatewayFixture.cs](../../tests/SharpClaw.Gateway.Tests/GatewayFixture.cs)):
  real Kestrel on two ephemeral ports, temp SQLite file, `Provider:Type=Fake`, and a
  `FakeTimeProvider` (`fx.Time`). Always `await using` the fixture. Don't use `WebApplicationFactory`
  test server — listener separation depends on real local ports.
- Reuse helpers instead of hand-rolling requests: `fx.CreateAgentAsync`, `CreateThreadAsync`,
  `PostUserMessageAsync`, `ClaimAsync`, `ChatAsync`, `ChatTestHelpers.ParseChat`,
  `TestHelpers.ReadSseEventsAsync`, `ChatTestHelpers.WriteScript` (FakeProvider JSON rules).
- **Time:** advance the clock with `fx.Time.Advance(...)`; derive test timestamps from
  `fx.Time.GetUtcNow()`, never `DateTime.UtcNow` (e.g. token expiry, budget day rollover).
- **Streaming pitfall:** a chat call is only recorded in `llm_calls` after the stream completes.
  `ChatAsync` buffers the whole response; if you use `ResponseHeadersRead`, read the body to the
  end before asserting DB state. To test client aborts, dispose the response mid-stream and poll
  the DB for the `cancelled` row.
- **SSE has no replay:** subscribe to `/api/threads/{id}/stream` (and give it ~200 ms) before
  triggering the event you assert on.
- Simulate provider behaviour with FakeProvider scripts (`error.type` = `http` / `timeout` /
  `midstream`, `"usage": null` for unknown usage) rather than mocking `IChatProvider`.
- Privacy tests: assert captured logs contain no message content, token or admin key.
- **Agent tests** drive `AgentLoop.ProcessOnceAsync` with a fake `IGatewayClient`/`ISoulProvider`;
  **CLI tests** use a fake `ICliGateway` (check stream-opened-before-post ordering).
- Use xUnit analyzer-friendly asserts (e.g. `Assert.Single(items, predicate)`); Release builds
  treat warnings as errors.
