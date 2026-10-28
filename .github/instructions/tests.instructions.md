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
  `fx.Time.GetUtcNow()`, never `DateTime.UtcNow` (e.g. token expiry, budget day rollover). Fake DTO
  timestamps in the agent and CLI tests use `TestClock.Now`.
- **Metrics:** every gateway in the test process shares the meter name; scope a `MeterListener`
  to `fx.App.Services.GetRequiredService<GatewayMetrics>().Meter` (by reference), especially when
  calling `RecordObservableInstruments()`.
- **Streaming pitfall:** a chat call is only recorded in `llm_calls` after the stream completes.
  `ChatAsync` buffers the whole response; if you use `ResponseHeadersRead`, read the body to the
  end before asserting DB state. To test client aborts, dispose the response mid-stream and poll
  the DB for the `cancelled` row.
- **SSE:** a fresh connect replays nothing, so open the stream with `fx.OpenStreamAsync(path)`
  (optionally with `lastEventId`) **before** triggering the event you assert on. The server
  subscribes before it sends headers, so the stream is live as soon as `OpenStreamAsync` returns —
  no delay needed. `ReadSseEventsAsync` skips the `ready` framing event; `ReadSseAsync` returns ids
  too. Posting a message emits `message.created` on the stream.
- **No sleeps.** Never wait with a fixed `Task.Delay`. Use `TestHelpers.EventuallyAsync(condition)`
  for "eventually true" (DB rows, metrics), `TestHelpers.WaitForLongPollAsync<TSignal>(fx)` before
  triggering a long-poll wake-up (inbox: `IAgentInboxSignal`, approvals: `ApprovalSignal`), and
  `TestHelpers.WaitForStreamedTextAsync(fx, thread)` for "a reply is mid-stream". In fakes, signal
  with a `TaskCompletionSource` rather than polling a field. Measure promptness with a `Stopwatch`.
- Simulate provider behaviour with FakeProvider scripts (`error.type` = `http` / `timeout` /
  `midstream`, `"usage": null` for unknown usage) rather than mocking `IChatProvider`.
- Privacy tests: assert captured logs contain no message content, token or admin key.
- **Agent tests** drive `AgentLoop.ProcessOnceAsync` with a fake `IGatewayClient`/`ISoulProvider`;
  **CLI tests** use a fake `ICliGateway` (check stream-opened-before-post ordering).
- **Organisation:** keep test files under about 300 lines. Large suites are `partial` classes split
  by theme (`AuthTests.Sessions.cs`, `ApprovalTests.Expiry.cs`, `CliTests.Chat.cs`) so they share
  private setup helpers; reuse those helpers rather than adding new bespoke `SetupAsync`s.
- Use xUnit analyzer-friendly asserts (e.g. `Assert.Single(items, predicate)`); Release builds
  treat warnings as errors.
