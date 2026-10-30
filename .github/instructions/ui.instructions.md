---
description: "Use when editing the SharpClaw web UI in ui/: React components, TanStack Query hooks, the API client, the SSE stream manager, styling, Vitest/MSW tests or Playwright e2e specs."
applyTo: "ui/**"
---
# SharpClaw Web UI Guidelines

Full developer guide: [docs/ui.md](../../docs/ui.md). Spec: [3.3](../../docs/sharpclaw-3.3-implementation-spec.md).
The gateway is the only trust boundary: the UI holds no secrets and talks only to `/api/*` on its own origin.

- **API calls:** always `call(() => api.GET(...))` from `src/api/client.ts` (typed by the generated
  `src/api/schema.d.ts`). Never use `fetch` directly, and never hand-edit `schema.d.ts`; after a
  contract change run `npm run gen:api` (see the snapshot workflow in copilot-instructions).
- **Server state:** TanStack Query only. Every query key comes from `keys` in
  `src/api/queryKeys.ts`, including invalidations in `src/api/eventEffects.ts`; no literal
  `['thread']`-style arrays. No Redux; keep extra client state minimal (React state/context, or the
  small external stores `LiveStore` and `NoticeStore`).
- **Live events:** `EventStreamManager` (`src/api/stream.ts`) owns the tab's single `EventSource`.
  Components never open their own stream. Event effects must be idempotent (replay can repeat events).
- **Feature layout:** `src/features/<area>/` holds pages, components and their query/mutation hooks.
  Keep pages thin: put data and mutation logic in hooks (`useXxx`) and markup in focused components.
  Flag any component growing past about 250 lines.
- **Styling:** Tailwind with the semantic tokens in `src/styles/index.css`. Never hard-code colours
  (no hex/rgb or arbitrary `bg-[#…]`). Radix primitives for dialogs, menus, tabs and toasts.
- **CSP is strict** (`style-src 'self'`, no inline scripts or styles, nothing from other origins).
  Don't add web fonts (the LCARS theme's bundled, same-origin Antonio is the one exception), CDNs, `<style>` injection or libraries that inject styles. `react-remove-scroll`
  is aliased to `src/lib/removeScroll.tsx` for this reason.
- **No secure context** (plain HTTP on the LAN): no `navigator.clipboard`, `crypto.randomUUID`,
  service workers or notifications. ESLint forbids them, plus `localStorage`/`sessionStorage`
  outside `src/lib/prefs.ts` and `dangerouslySetInnerHTML`.
- **Privacy:** never log, persist or send prompt/reply text, tool arguments or results, tokens or
  passwords anywhere except to the gateway. Agent tokens live only in the reveal dialog's state.
- **Server clock:** render relative times, countdowns and staleness from `useServerNow()` (or
  `RelativeTime`), never `serverNow()` during render; the offset may arrive after first render.
- **Errors:** user-facing text for error codes and fail reasons comes from `src/api/errors.ts`.
- **Tests:** Vitest + Testing Library + MSW (`tests/msw.ts`) with the fake `EventSource` from
  `tests/utils.tsx`; add a `jest-axe` check for new screens. Prefer role/label queries. Use fake
  timers for reconnect/backoff logic rather than real waits.
- **E2E:** Playwright in `e2e/`, run against the isolated stack from `e2e/stack.sh`
  (`npm run build` first). Wait on locators or responses, never `page.waitForTimeout`.
- **Before committing:** `npm run lint && npm run typecheck && npm test && npm run check:api && npm run build`.
