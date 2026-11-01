# SharpClaw V3 — Attachments Spec (files in the prompt)

**Audience:** an implementing coding agent (and the human reviewing its work).
**Status:** **implemented** (tasks 1–4 in section 9).
**Scope:** let a user attach text files and images to a message in the web UI, so the model sees
them with the prompt. This lifts "attachments and file upload" out of the 3.3 out-of-scope list;
everything else there still stands. Earlier specs apply wherever this one doesn't change them, in
particular context management ([pre-3.3 foundations](sharpclaw-pre-3.3-foundations-spec.md) §6)
and the chat completion proxy ([3.1](sharpclaw-3.1-implementation-spec.md)).

## 0. How to use this document

- Implement in the task order in section 9, each with passing tests.
- "MUST" / "MUST NOT" are hard requirements. If something is ambiguous, stop and ask.
- Never log, put in an event, or emit as a metric label any attachment name or content.

### 0.1 Decisions

| # | Question | Decision |
|---|---|---|
| Q1 | Where do attachments live? | **In the gateway database** (`message_attachments`), with the user message. They are not copied into the agent's workspace. |
| Q2 | How does the model see them? | **Inline in the turn.** Text files are inlined into the user message text by the context builder. Images go to the provider as OpenAI `image_url` content parts. |
| Q3 | How are they uploaded? | **Base64 in the post-message JSON** (`attachments` on `POST /api/threads/{id}/messages`). No separate upload endpoint. |
| Q4 | Trust | **Trusted**, like typed text. Attaching a file does not taint the turn. |
| Q5 | Later turns | Attachments **stay in the history** and are replayed like the message text until context truncation drops their turn. |
| Q6 | Clients | **Web UI only** (attach button, drag and drop, paste). The CLI is unchanged. |
| Q7 | How do images get to the provider? | **By reference.** Context and agent requests carry attachment ids, not bytes. The gateway resolves an id to a `data:` URI only on the provider call. This keeps the agent's requests small. Agents also can never pass a remote image URL, which would make the provider fetch arbitrary URLs and bypass egress. |

## 1. Limits and types

| Rule | Value |
|---|---|
| Attachments per message | at most `Messages:MaxAttachments` (default **5**) |
| Image types | `.png`, `.jpg`/`.jpeg`, `.gif`, `.webp`; the bytes MUST match the extension's magic number. SVG is not accepted. |
| Image size | at most `Messages:MaxImageBytes` (default **5 MB**) each |
| Text types | the extensions in `AttachmentRules.TextExtensions` (txt, md, csv, json, yaml, xml, log, common code files…) plus `Dockerfile` and `Makefile`. The bytes MUST be valid UTF-8 with no NUL. |
| Text size | at most `Messages:MaxTextBytes` (default **256 KB**) each |
| File name | the last path segment, control characters removed, 1–255 chars |
| Image token estimate | `Context:ImageTokens` (default **1000**) per image, for context budgeting |

The kind (`image` or `text`) comes from the extension. The client's declared media type is not
trusted. The gateway stores images with their real media type and text as
`text/plain; charset=utf-8`. With attachments, `content` may be empty. Without any, it is still
required.

## 2. Data (task 1)

`message_attachments`: `id` (v7 guid), `message_id` (FK → `messages`, cascade), `thread_id`,
`position` (order within the message), `kind` (`image`/`text`), `file_name`, `media_type`, `size_bytes`, `data` (blob), `created_at`.
Index on `message_id`. Rows are written in the same transaction as their user message.

## 3. Client API (task 1)

- `POST /api/threads/{id}/messages`: optional `attachments: [{ fileName, data }]`, where `data`
  is base64. Any rule in section 1 that fails returns `400 validation_failed`, naming the file and
  the rule. The endpoint's request body limit is raised to fit the largest valid request.
- `MessageDto.attachments`: `[{ id, fileName, kind, mediaType, sizeBytes }]` on user messages, in
  the order they were sent; null when there are none.
- `GET /api/threads/{threadId}/attachments/{attachmentId}`: the bytes, with the stored media
  type, `X-Content-Type-Options: nosniff` and `Content-Disposition` (`inline` for images,
  `attachment` for text). Like every `/api` response it is `Cache-Control: no-store`. An id not in
  that thread returns `404`. It is on the client listener only, with the usual client auth.

## 4. Context (task 2)

`ModelReplay` builds a user message with attachments like this:

- After the message text, each text attachment is appended as
  ``[Attached file: <name>]`` followed by a fenced block. The fence is one backtick longer than
  the longest run of backticks in the file, and at least three.
- Each image adds an ``[Attached image: <name>]`` line, and its id goes in `image_ids` on the
  context message.

`ContextMessage` and the agent's `ChatMessage` gain `image_ids` (snake_case on the wire; omitted
when empty). `TokenEstimator` adds `ImageTokens` per image id.

## 5. Chat completions (task 2)

- `/v1/chat/completions` accepts an optional `image_ids` (array of GUID strings) on **user**
  messages only. Every id MUST be an image attachment of the request's `X-Thread-Id` thread.
  Otherwise the request fails with `400 validation_failed`. The gateway loads the bytes and adds
  them to the `ProviderChatMessage`.
- The preflight token estimate counts `ImageTokens` per image.
- `OpenRouterProvider` writes a message with images as a content array: a `text` part (when the
  text is non-empty) and one `image_url` part per image (`data:<media type>;base64,…`). Messages
  without images keep string content.
- The agent does not touch `image_ids`: it copies them from the context into its chat requests.

## 6. Web UI (task 3)

- The composer gets an attach button (a hidden `<input type="file" multiple>` whose `accept`
  lists the allowed extensions), drag and drop onto the composer, and image paste.
- Pending attachments show as removable chips (image thumbnail via a `data:` URL, or a file icon
  with the size). The UI checks the section 1 rules before sending and shows a toast for each
  rejected file. The server is still the authority.
- Sent messages show their attachments. Images are thumbnails loaded from the download endpoint
  (same origin, allowed by the CSP). Text files are download links.

## 7. Docs (task 4)

Update the OpenAPI contract (and regenerate `ui/src/api/schema.d.ts`), the README, `architecture.md`
and `ui.md`.

## 8. Out of scope

CLI attachments; PDFs and other binary documents; attachments from agents (`ask_agent`) or in
replies; deleting or editing attachments; a separate upload endpoint; virus scanning; image
resizing or re-encoding; per-model vision capability checks (a model without vision fails
upstream, and the turn fails like any other upstream error).

## 9. Tasks

| # | Task |
|---|---|
| 1 | Entity, migration, options, post validation and storage, DTO, download endpoint; gateway tests |
| 2 | Context inlining, `image_ids` through context, agent and `/v1`; provider content parts; tests |
| 3 | Web UI composer and message rendering; vitest tests |
| 4 | OpenAPI, README, architecture and UI docs |
