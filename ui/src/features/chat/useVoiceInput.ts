import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, errorMessage } from '../../api/errors';
import { microphoneErrorMessage, toSpeechWav, transcribe, voiceUnavailableReason } from './voice';

export type VoicePhase = 'idle' | 'starting' | 'recording' | 'transcribing';

interface Session {
  recorder: MediaRecorder;
  stream: MediaStream;
  chunks: Blob[];
  timer: number;
  startedAt: number;
}

/**
 * Records from the microphone, then has the gateway transcribe it and hands over the text.
 * `cancel` drops a recording, or the result of a transcription already under way. The microphone
 * is released as soon as recording stops, and on unmount.
 */
export function useVoiceInput({
  maxSeconds,
  onTranscript,
}: {
  maxSeconds: number;
  onTranscript: (text: string) => Promise<void>;
}) {
  const [phase, setPhase] = useState<VoicePhase>('idle');
  const [elapsed, setElapsed] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const session = useRef<Session | null>(null);
  // Bumped by cancel: a recording or transcription from an older generation is ignored.
  const generation = useRef(0);
  const onTranscriptRef = useRef(onTranscript);
  useEffect(() => {
    onTranscriptRef.current = onTranscript;
  });

  const release = (s: Session) => {
    window.clearInterval(s.timer);
    s.stream.getTracks().forEach((t) => t.stop());
    if (session.current === s) session.current = null;
  };

  const finish = useCallback(
    async (recording: Blob, mine: number) => {
      setPhase('transcribing');
      try {
        const text = await transcribe(await toSpeechWav(recording, maxSeconds));
        if (generation.current !== mine) return;
        if (!text) setError('No speech was recognised. Try again, a little closer to the microphone.');
        else await onTranscriptRef.current(text);
      } catch (e) {
        if (generation.current === mine)
          setError(
            e instanceof ApiError ? errorMessage(e) : "The recording couldn't be processed. Try again.",
          );
      } finally {
        if (generation.current === mine) setPhase('idle');
      }
    },
    [maxSeconds],
  );

  const start = useCallback(async () => {
    if (session.current) return;
    // Explained on click rather than by disabling the button, so touch users see why too.
    const unavailable = voiceUnavailableReason();
    if (unavailable) {
      setError(unavailable);
      return;
    }
    const mine = ++generation.current;
    setError(null);
    setPhase('starting');
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true },
      });
    } catch (e) {
      if (generation.current === mine) {
        setError(microphoneErrorMessage(e));
        setPhase('idle');
      }
      return;
    }
    if (generation.current !== mine) {
      stream.getTracks().forEach((t) => t.stop()); // cancelled while the permission prompt was up
      return;
    }

    const recorder = new MediaRecorder(stream);
    const s: Session = { recorder, stream, chunks: [], timer: 0, startedAt: Date.now() };
    recorder.ondataavailable = (e) => {
      if (e.data.size > 0) s.chunks.push(e.data);
    };
    recorder.onstop = () => {
      release(s);
      if (generation.current !== mine) return;
      if (s.chunks.length === 0) {
        setError('Nothing was recorded. Try again.');
        setPhase('idle');
        return;
      }
      void finish(new Blob(s.chunks, { type: recorder.mimeType }), mine);
    };
    // Stops by itself at the gateway's limit.
    s.timer = window.setInterval(() => {
      const seconds = (Date.now() - s.startedAt) / 1000;
      setElapsed(Math.min(seconds, maxSeconds));
      if (seconds >= maxSeconds && recorder.state !== 'inactive') recorder.stop();
    }, 250);
    session.current = s;
    setElapsed(0);
    recorder.start();
    setPhase('recording');
  }, [finish, maxSeconds]);

  /** Stops recording and transcribes what was said. */
  const stop = useCallback(() => {
    const s = session.current;
    if (s && s.recorder.state !== 'inactive') s.recorder.stop();
  }, []);

  /** Drops the recording (or the pending transcript) without sending anything. */
  const cancel = useCallback(() => {
    generation.current++;
    const s = session.current;
    if (s) {
      if (s.recorder.state !== 'inactive') s.recorder.stop();
      release(s);
    }
    setPhase('idle');
  }, []);

  useEffect(
    () => () => {
      generation.current++;
      const s = session.current;
      if (s) {
        if (s.recorder.state !== 'inactive') s.recorder.stop();
        release(s);
      }
    },
    [],
  );

  return { phase, elapsed, error, start, stop, cancel, clearError: () => setError(null) };
}
