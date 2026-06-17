---
sidebar_position: 16
---

# Speech-to-Text (STT)

SharpClaw can transcribe voice and audio messages on Telegram and the web chat using
[Whisper.net](https://github.com/sandrohanea/whisper.net) — C# bindings for `whisper.cpp`.
All inference runs locally on CPU. No GPU or Python dependency.

## How it works

1. A user sends a voice message (Telegram) or uploads audio (web chat).
2. SharpClaw downloads the audio bytes and pipes them to `ffmpeg`, which converts to
   16 kHz mono PCM WAV.
3. The WAV stream is passed to a shared `WhisperFactory` (one ggml model loaded once,
   per-call processors created on top).
4. The transcript is fed back into the normal agent invocation pipeline, exactly as
   if the user had typed it.

## Requirements

- **`ffmpeg`** must be installed and on `PATH`. On Debian/Ubuntu: `sudo apt install ffmpeg`.
- A ggml whisper model file. By default SharpClaw will download the `base` model from
  HuggingFace on first use (~150 MB).

## Configuration

```json
{
  "Stt": {
    "Enabled": false,
    "ModelSize": "base",
    "ModelDirectory": "models/whisper",
    "AutoDownload": true,
    "Language": "auto",
    "MaxConcurrency": 1,
    "FfmpegPath": "ffmpeg",
    "MaxAudioBytes": 26214400
  }
}
```

| Option | Description |
|--------|-------------|
| `Enabled` | Master switch. When `false`, no STT services are registered. |
| `ModelSize` | One of `tiny`, `base`, `small`, `medium`, `large-v3` (or `.en` variants). |
| `ModelDirectory` | Where ggml `.bin` model files live. |
| `AutoDownload` | If `true`, missing models are downloaded from HuggingFace on first request. |
| `Language` | ISO code (e.g. `en`, `de`) or `auto` for whisper to detect. |
| `MaxConcurrency` | Bounded semaphore — number of concurrent transcriptions. |
| `FfmpegPath` | Path to the ffmpeg executable. |
| `MaxAudioBytes` | Reject audio uploads larger than this (web endpoint). |

## Model size guide

| Size | Disk | RAM | Quality |
|------|------|-----|---------|
| `tiny` | 75 MB | ~390 MB | Lowest, fastest |
| `base` | 150 MB | ~500 MB | Good default |
| `small` | 500 MB | ~1 GB | Better accuracy |
| `medium` | 1.5 GB | ~2.6 GB | High accuracy |
| `large-v3` | 3 GB | ~4.7 GB | Best |

`.en` variants are English-only and slightly more accurate for English speech.

## Pre-downloading a model

```bash
./scripts/download-whisper-model.sh base
```

## Telegram

When `Stt:Enabled = true`, voice messages, audio files, and round video notes sent to
the bot are automatically transcribed. The transcript is echoed back to the user as a
quoted line (`🎤 _your text_`) so they can verify what was heard, then routed to the
agent as if they had typed it. If the message also has a caption, the caption is
prepended to the transcript.

## Web chat

`POST /api/chat/{agentName}/audio` accepts `multipart/form-data` with an `audio` file
field and an optional `language` field. It returns:

```json
{
  "transcript": "what the model heard",
  "response": "the agent's reply",
  "switchedTo": null
}
```

## Operational notes

- The ggml model is loaded **lazily on first request** and kept in memory for the
  process lifetime.
- Concurrency is bounded by `Stt:MaxConcurrency`. Whisper inference is CPU-heavy;
  increase only if you have spare cores.
- If transcription fails (ffmpeg missing, model download blocked, etc.) the user
  receives a clear error message — the agent invocation is skipped, not retried.
