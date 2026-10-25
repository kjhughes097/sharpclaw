# SharpClaw V3

A personal multi-agent system. Phase 3.1 proves the architecture end to end:

> CLI → gateway → agent → gateway → OpenRouter → back to the CLI, streamed,
> with the agent network-isolated, every LLM call audited, a daily spend cap
> enforced, and telemetry flowing to Grafana.

The **gateway** is the single trust boundary. Agents are untrusted workloads:
they hold no provider keys, have no direct internet access, and reach the world
only through the gateway.

See [docs/sharpclaw-3.1-implementation-spec.md](docs/sharpclaw-3.1-implementation-spec.md)
for the full specification.

## Projects

- `src/SharpClaw.Contracts` — DTOs, error shapes, constants shared by all projects
- `src/SharpClaw.Gateway` — ASP.NET gateway (two listeners), EF Core/SQLite, providers, telemetry
- `src/SharpClaw.Agent` — the agent loop (console app, one per Docker container)
- `src/SharpClaw.Cli` — admin and chat client (assembly name: `sharpclaw`)

## Build & test

```bash
dotnet build
dotnet test
```

Setup, bootstrap, configuration and manual verification results are documented
further down once the stack is in place.
