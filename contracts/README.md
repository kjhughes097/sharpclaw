# Client contract snapshots

The gateway's client API contract, committed so that changes to it are always deliberate. Both
test suites check against these files; nothing here is edited by hand.

| Path                               | What                                                            | Checked by                                                                                                                           |
| ---------------------------------- | --------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| `openapi/sharpclaw-client.v1.json` | OpenAPI document for the client routes (`GET /openapi/v1.json`) | `SnapshotTests` (gateway); `npm run check:api` (UI types in `ui/src/api/schema.d.ts` are generated from it)                          |
| `events/<type>.json`               | A sample payload for every SSE event type                       | `SnapshotTests` (the gateway's serialized payloads match); `ui/tests/events.test.ts` (the UI's types and event handling accept them) |

After changing a DTO, endpoint or event deliberately:

```bash
UPDATE_OPENAPI=1 dotnet test tests/SharpClaw.Gateway.Tests --filter "FullyQualifiedName~SnapshotTests"
cd ui && npm run gen:api
```

Review the diff and commit these files with the change.
