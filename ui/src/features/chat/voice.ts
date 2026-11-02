import { api, call } from '../../api/client';

/** The gateway takes 16 kHz mono 16-bit PCM WAV only, which is what whisper wants (voice spec section 2). */
export const SPEECH_SAMPLE_RATE = 16000;

/**
 * Why this page can't record, or null when it can. Browsers only expose the microphone on secure
 * pages (HTTPS or localhost), so over plain HTTP on the LAN voice input is unavailable while the
 * rest of the UI works as usual.
 */
export function voiceUnavailableReason(): string | null {
  if (typeof window === 'undefined' || !window.isSecureContext)
    return 'Voice input needs HTTPS or localhost: browsers only allow the microphone on secure pages.';
  if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined')
    return "This browser can't record audio.";
  return null;
}

/** Plain text for a failed `getUserMedia`. */
export function microphoneErrorMessage(error: unknown): string {
  const name = error instanceof DOMException ? error.name : '';
  if (name === 'NotAllowedError' || name === 'SecurityError')
    return "Microphone access was blocked. Allow it in the browser's site settings and try again.";
  if (name === 'NotFoundError' || name === 'OverconstrainedError') return 'No microphone was found.';
  if (name === 'NotReadableError') return 'The microphone is in use by another application.';
  return "The microphone couldn't be started.";
}

/** Mono samples in [-1, 1] as a 16-bit PCM WAV file. */
export function encodeWav(samples: Float32Array, sampleRate = SPEECH_SAMPLE_RATE): Blob {
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const view = new DataView(buffer);
  const ascii = (offset: number, text: string) => {
    for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
  };
  ascii(0, 'RIFF');
  view.setUint32(4, 36 + samples.length * 2, true);
  ascii(8, 'WAVE');
  ascii(12, 'fmt ');
  view.setUint32(16, 16, true); // fmt chunk size
  view.setUint16(20, 1, true); // PCM
  view.setUint16(22, 1, true); // mono
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true); // byte rate
  view.setUint16(32, 2, true); // block align
  view.setUint16(34, 16, true); // bits per sample
  ascii(36, 'data');
  view.setUint32(40, samples.length * 2, true);
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i]!));
    view.setInt16(44 + i * 2, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  return new Blob([buffer], { type: 'audio/wav' });
}

/**
 * Converts the browser's recording (webm/opus, mp4/aac… whatever MediaRecorder produced) into the
 * WAV the gateway accepts: decoded, mixed down to mono and resampled to 16 kHz by an offline audio
 * context. At most `maxSeconds` are kept, so a recording stopped a moment late still fits the limit.
 */
export async function toSpeechWav(recording: Blob, maxSeconds: number): Promise<Blob> {
  const context = new AudioContext();
  let decoded: AudioBuffer;
  try {
    decoded = await context.decodeAudioData(await recording.arrayBuffer());
  } finally {
    void context.close();
  }
  const frames = Math.max(
    1,
    Math.min(Math.ceil(decoded.duration * SPEECH_SAMPLE_RATE), maxSeconds * SPEECH_SAMPLE_RATE),
  );
  const offline = new OfflineAudioContext(1, frames, SPEECH_SAMPLE_RATE);
  const source = offline.createBufferSource();
  source.buffer = decoded;
  source.connect(offline.destination);
  source.start();
  const rendered = await offline.startRendering();
  return encodeWav(rendered.getChannelData(0));
}

/** `POST /api/transcriptions`: the text spoken in a speech WAV, transcribed on the gateway. */
export async function transcribe(wav: Blob): Promise<string> {
  const { text } = await call(() =>
    api.POST('/api/transcriptions', {
      // The contract types a binary body as a string; send the Blob as-is.
      body: wav as unknown as string,
      bodySerializer: (body) => body,
      headers: { 'Content-Type': 'audio/wav' },
    }),
  );
  return text.trim();
}

/** `m:ss`. */
export function formatSeconds(seconds: number): string {
  const whole = Math.floor(seconds);
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}
