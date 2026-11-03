import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { clearDraftsForTests } from '../src/features/chat/Composer';
import { encodeWav } from '../src/features/chat/voice';
import { fixtures, NOW, server } from './msw';
import { renderApp } from './utils';

const T = 'thread-1';
type Posted = { content: string; attachments?: unknown[] | null };

// ----- Browser stand-ins: jsdom has no microphone, MediaRecorder or Web Audio -----

const track = { stop: vi.fn() };
const getUserMedia = vi.fn();

class FakeRecorder {
  static count = 0;
  state: 'inactive' | 'recording' = 'inactive';
  mimeType = 'audio/webm;codecs=opus';
  ondataavailable: ((e: { data: Blob }) => void) | null = null;
  onstop: (() => void) | null = null;
  constructor() {
    FakeRecorder.count++;
  }
  start() {
    this.state = 'recording';
  }
  stop() {
    this.state = 'inactive';
    // Like a browser: the last data, then stop, after the current task.
    queueMicrotask(() => {
      this.ondataavailable?.({ data: new Blob(['opus frames'], { type: this.mimeType }) });
      this.onstop?.();
    });
  }
}

/** The recording decodes to `decodedSeconds` of audio; resampling yields one constant sample. */
let decodedSeconds = 1.5;
class FakeAudioContext {
  decodeAudioData = () => Promise.resolve({ duration: decodedSeconds });
  close = () => Promise.resolve();
}
class FakeOfflineAudioContext {
  destination = {};
  constructor(
    readonly channels: number,
    readonly frames: number,
    readonly rate: number,
  ) {}
  createBufferSource() {
    return { buffer: null, connect() {}, start() {} };
  }
  startRendering() {
    return Promise.resolve({ getChannelData: () => new Float32Array(this.frames).fill(0.5) });
  }
}

function secure(value: boolean) {
  Object.defineProperty(window, 'isSecureContext', { value, configurable: true });
}

beforeEach(() => {
  clearDraftsForTests();
  track.stop.mockReset();
  getUserMedia.mockReset();
  getUserMedia.mockResolvedValue({ getTracks: () => [track] });
  FakeRecorder.count = 0;
  decodedSeconds = 1.5;
  secure(true);
  Object.defineProperty(navigator, 'mediaDevices', { value: { getUserMedia }, configurable: true });
  vi.stubGlobal('MediaRecorder', FakeRecorder);
  vi.stubGlobal('AudioContext', FakeAudioContext);
  vi.stubGlobal('OfflineAudioContext', FakeOfflineAudioContext);
});

afterEach(() => {
  vi.unstubAllGlobals();
  secure(false);
});

// ----- Gateway -----

function useGateway({
  maxSeconds = 120 as number | null,
  transcript = 'what is on my calendar today',
  transcribeStatus = 200,
  postStatus = 202,
} = {}) {
  const state = { posts: [] as Posted[], uploads: [] as { type: string | null; bytes: Uint8Array }[] };
  server.use(
    http.get('*/api/system', () =>
      HttpResponse.json({
        version: 'abc1234',
        provider: 'Fake',
        utcNow: NOW,
        budgetResetAtUtc: '2026-10-26T00:00:00Z',
        models: [],
        transcriptionMaxSeconds: maxSeconds,
      }),
    ),
    http.get('*/api/threads/:id', () =>
      HttpResponse.json({
        thread: {
          id: T,
          agentId: 'agent-1',
          title: 'Voice',
          createdAt: '2026-10-25T10:00:00Z',
          archived: false,
          lastMessageAt: '2026-10-25T11:00:00Z',
          context: { droppedTurns: 0, estimatedTokens: 100, windowTokens: 32000 },
        },
        agent: fixtures.agent(),
        inFlight: null,
        queued: 0,
        pendingApprovals: [],
      }),
    ),
    http.get('*/api/threads/:id/messages', () => HttpResponse.json({ messages: [], hasMoreBefore: false })),
    http.get('*/api/threads/:id/trace', () => HttpResponse.json({ turns: [] })),
    http.get('*/api/threads', () => HttpResponse.json({ threads: [fixtures.thread()], nextCursor: null })),
    http.post('*/api/transcriptions', async ({ request }) => {
      state.uploads.push({
        type: request.headers.get('Content-Type'),
        bytes: new Uint8Array(await request.arrayBuffer()),
      });
      return transcribeStatus === 200
        ? HttpResponse.json({ text: transcript })
        : HttpResponse.json(
            { error: { code: 'transcription_unavailable', message: 'off' } },
            { status: transcribeStatus },
          );
    }),
    http.post('*/api/threads/:id/messages', async ({ request }) => {
      state.posts.push((await request.json()) as Posted);
      return postStatus === 202
        ? HttpResponse.json(
            { message: { id: 'posted-1', status: 'pending', createdAt: NOW } },
            { status: 202 },
          )
        : HttpResponse.json({ error: { code: 'budget_exceeded', message: 'over' } }, { status: postStatus });
    }),
  );
  return state;
}

const ascii = (bytes: Uint8Array, from: number, to: number) => String.fromCharCode(...bytes.slice(from, to));

describe('voice input', () => {
  it('has no microphone button when the gateway has no transcription model', async () => {
    useGateway({ maxSeconds: null });
    renderApp(`/prompts/${T}`);
    await screen.findByLabelText('Message');
    await screen.findByRole('button', { name: 'Attach files' });

    expect(screen.queryByRole('button', { name: 'Record a voice message' })).not.toBeInTheDocument();
  });

  it('records, has the gateway transcribe it and sends the words after the typed draft', async () => {
    const state = useGateway();
    const { user } = renderApp(`/prompts/${T}`);
    await user.type(await screen.findByLabelText('Message'), 'Quick question.');

    await user.click(await screen.findByRole('button', { name: 'Record a voice message' }));
    expect(await screen.findByText(/Recording 0:00 \/ 2:00/)).toHaveAttribute('role', 'status');
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Stop and send' }));

    await waitFor(() => expect(state.posts).toHaveLength(1));
    expect(state.posts[0]!).toEqual({
      content: 'Quick question.\n\nwhat is on my calendar today',
      unattended: false,
      attachments: null,
    });
    const upload = state.uploads[0]!;
    expect(upload.type).toBe('audio/wav');
    expect(ascii(upload.bytes, 0, 4)).toBe('RIFF');
    expect(upload.bytes.length).toBe(44 + 1.5 * 16000 * 2);
    expect(getUserMedia).toHaveBeenCalledWith({
      audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true },
    });
    expect(track.stop).toHaveBeenCalled();
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue(''));
  });

  it('explains why voice input is unavailable over plain HTTP', async () => {
    useGateway();
    secure(false);
    const { user } = renderApp(`/prompts/${T}`);

    await user.click(await screen.findByRole('button', { name: 'Record a voice message' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Voice input needs HTTPS or localhost');
    expect(getUserMedia).not.toHaveBeenCalled();
  });

  it('explains a blocked microphone', async () => {
    useGateway();
    getUserMedia.mockRejectedValue(new DOMException('denied', 'NotAllowedError'));
    const { user } = renderApp(`/prompts/${T}`);

    await user.click(await screen.findByRole('button', { name: 'Record a voice message' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Microphone access was blocked.');
    expect(FakeRecorder.count).toBe(0);
  });

  it('discards a recording without uploading it and releases the microphone', async () => {
    const state = useGateway();
    const { user } = renderApp(`/prompts/${T}`);

    await user.click(await screen.findByRole('button', { name: 'Record a voice message' }));
    await user.click(await screen.findByRole('button', { name: 'Discard recording' }));

    expect(await screen.findByRole('button', { name: 'Record a voice message' })).toBeEnabled();
    expect(track.stop).toHaveBeenCalled();
    expect(state.uploads).toHaveLength(0);
    expect(state.posts).toHaveLength(0);
  });

  it('sends nothing when no speech was recognised', async () => {
    const state = useGateway({ transcript: '' });
    const { user } = renderApp(`/prompts/${T}`);

    await user.click(await screen.findByRole('button', { name: 'Record a voice message' }));
    await user.click(await screen.findByRole('button', { name: 'Stop and send' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('No speech was recognised.');
    expect(state.uploads).toHaveLength(1);
    expect(state.posts).toHaveLength(0);
  });

  it('shows a transcription error', async () => {
    const state = useGateway({ transcribeStatus: 503 });
    const { user } = renderApp(`/prompts/${T}`);

    await user.click(await screen.findByRole('button', { name: 'Record a voice message' }));
    await user.click(await screen.findByRole('button', { name: 'Stop and send' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Voice input is off on this gateway.');
    expect(state.posts).toHaveLength(0);
  });

  it('keeps the words in the draft when the message cannot be sent', async () => {
    const state = useGateway({ postStatus: 429 });
    const { user } = renderApp(`/prompts/${T}`);

    await user.click(await screen.findByRole('button', { name: 'Record a voice message' }));
    await user.click(await screen.findByRole('button', { name: 'Stop and send' }));

    await waitFor(() => expect(state.posts).toHaveLength(1));
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue('what is on my calendar today'));
  });

  it('stops by itself at the limit and uploads no more than the limit', async () => {
    const state = useGateway({ maxSeconds: 1 });
    const { user } = renderApp(`/prompts/${T}`);

    await user.click(await screen.findByRole('button', { name: 'Record a voice message' }));

    await waitFor(() => expect(state.posts).toHaveLength(1), { timeout: 3000 });
    // The recording decodes to 1.5 s; only the first second is sent.
    expect(state.uploads[0]!.bytes.length).toBe(44 + 16000 * 2);
  });
});

describe('encodeWav', () => {
  it('writes a 16 kHz mono 16-bit PCM header and clamps samples', async () => {
    const wav = encodeWav(new Float32Array([0, 0.5, -1, 2]));
    const bytes = new Uint8Array(await wav.arrayBuffer());
    const view = new DataView(bytes.buffer);

    expect(wav.type).toBe('audio/wav');
    expect(ascii(bytes, 0, 4) + ascii(bytes, 8, 16) + ascii(bytes, 36, 40)).toBe('RIFFWAVEfmt data');
    expect(view.getUint16(20, true)).toBe(1); // PCM
    expect(view.getUint16(22, true)).toBe(1); // mono
    expect(view.getUint32(24, true)).toBe(16000);
    expect(view.getUint16(34, true)).toBe(16);
    expect(view.getUint32(40, true)).toBe(8);
    expect([0, 1, 2, 3].map((i) => view.getInt16(44 + i * 2, true))).toEqual([0, 16383, -32768, 32767]);
  });
});
