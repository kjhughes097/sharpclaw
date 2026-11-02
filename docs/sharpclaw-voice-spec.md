# SharpClaw V3 — Voice Input Spec (speak a prompt)

**Audience:** an implementing coding agent (and the human reviewing its work).
**Status:** **implemented** (tasks 1–3 in section 7).
**Scope:** let a user record a spoken message in the web UI. The gateway transcribes it on this
machine (CPU) and the UI sends the text as an ordinary user message. Earlier specs apply wherever
this one doesn't change them.

## 0. Decisions

| # | Question | Decision |
|---|---|---|
| Q1 | Where is speech transcribed? | **In the gateway, in-process**, with whisper.cpp through Whisper.net, on the CPU. The gateway is the trust boundary and the only container with a writable data dir and no isolation constraints; agents never see audio. Audio never leaves the host. |
| Q2 | What audio does the gateway accept? | **Only 16 kHz, mono, 16-bit PCM WAV**, what whisper wants. The browser converts its recording (webm/opus, mp4/aac…) with Web Audio before upload, so the gateway needs no codecs or ffmpeg. |
| Q3 | How does the text become a prompt? | **Two calls.** `POST /api/transcriptions` returns `{ text }`; the UI then posts it with `POST /api/threads/{id}/messages`, after any typed draft and with any pending attachments. The message is ordinary text: nothing marks it as spoken. |
| Q4 | Review before sending? | **No.** Stopping the recording sends it. If the send fails, the words go into the draft so they can be fixed and sent by hand. |
| Q5 | Is audio stored? | **Never.** It is held in memory for the request only; neither audio nor transcript is logged, put in an event or used as a metric label. |
| Q6 | Which model? | A ggml whisper model file named by `Transcription:ModelPath`. The gateway image bundles `base.en` (148 MB, sha256-pinned) at build time; no download at runtime. Empty path: voice input is off. |
| Q7 | Plain HTTP on the LAN? | Browsers only allow the microphone on secure pages (HTTPS or `localhost`). Elsewhere the microphone button explains this when clicked; the rest of the UI is unaffected. |

## 1. Configuration

| Setting | Default | Notes |
|---|---|---|
| `Transcription:ModelPath` | empty (image: `/app/models/ggml-base.en.bin`) | Empty turns voice input off. A path that doesn't exist fails startup. |
| `Transcription:Language` | `en` | A whisper language code, or `auto`. `.en` models only do English. |
| `Transcription:MaxSeconds` | `120` | Longest recording accepted (1–600). |
| `Transcription:Threads` | `4` | CPU threads per transcription (1–64). |

The model loads on first use and stays loaded. One transcription runs at a time; others wait.
The image build takes `WHISPER_MODEL` (default `base.en`; empty for none) and
`WHISPER_MODEL_SHA256`; `scripts/build-images.sh` passes both through from the environment. The
runtime image adds `libgomp1` (OpenMP, needed by whisper.cpp).

## 2. Audio

A RIFF/WAVE file with a `fmt ` chunk (PCM, 1 channel, 16000 Hz, 16 bits) before a non-empty
`data` chunk; other chunks are skipped. Anything else is `400 validation_failed` with the reason.

## 3. Client API

- `POST /api/transcriptions` (client listener, usual client auth and CSRF): body `audio/wav`.
  - `200 { "text": "…" }`; `text` is trimmed and empty when no speech was recognised (whisper's
    non-speech tags such as `[BLANK_AUDIO]` are dropped).
  - `400 validation_failed`: not acceptable audio (section 2).
  - `413 validation_failed`: longer than `MaxSeconds`, or a body over the endpoint's limit
    (`MaxSeconds` of audio plus 4 KB).
  - `503 transcription_unavailable`: no model configured.
- `GET /api/system` adds `transcriptionMaxSeconds`: `MaxSeconds`, or null when voice input is off.
- The UI's `Permissions-Policy` allows `microphone=(self)` (camera, geolocation and payment stay off).

## 4. Web UI

- When `transcriptionMaxSeconds` is set, the composer shows a 🎤 button next to 📎.
- Clicking it asks for the microphone and records with `MediaRecorder`. While recording, a status
  line shows the elapsed time against the limit, Send is disabled, and the button becomes
  **■ Stop and send** plus **✕ Discard**. Recording stops by itself at the limit.
- Stopping decodes the recording, mixes it to mono and resamples it to 16 kHz with an
  `OfflineAudioContext` (keeping at most `transcriptionMaxSeconds`), uploads the WAV, then sends
  the text (section 0 Q3/Q4). Discarding, switching threads or leaving the page drops the
  recording and any transcription in progress, and releases the microphone.
- Errors (microphone blocked or missing, insecure page, no speech, transcription failure) show
  inline above the composer.

## 5. Telemetry

`sharpclaw.transcription.duration` (histogram, seconds, label `status` = `ok`|`error`). One log
line per transcription with the audio length, time taken and transcript length only.

## 6. Out of scope

Spoken replies (text to speech); streaming or live transcription; voice in the CLI; storing or
replaying audio; marking messages as spoken; GPU acceleration; choosing the model or language
from the UI; voice input for agents or tools.

## 7. Tasks

| # | Task |
|---|---|
| 1 | Options, WAV parser, `ITranscriber`/`WhisperTranscriber`, endpoint, system flag, metric, Permissions-Policy; gateway tests |
| 2 | Web UI recording, conversion, upload and send; vitest tests |
| 3 | Gateway image (model, `libgomp1`), build script, OpenAPI, README, architecture, UI and telemetry docs |
