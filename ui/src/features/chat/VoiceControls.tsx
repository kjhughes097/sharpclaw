import { Button, Spinner } from '../../components/ui';
import type { useVoiceInput } from './useVoiceInput';
import { formatSeconds, voiceUnavailableReason } from './voice';

type Voice = ReturnType<typeof useVoiceInput>;

/** What voice input is doing (or why it failed), shown above the composer's input row. */
export function VoiceStatus({ voice, maxSeconds }: { voice: Voice; maxSeconds: number }) {
  if (voice.error)
    return (
      <p role="alert" className="px-1 pb-1 text-xs text-danger">
        {voice.error}
      </p>
    );
  if (voice.phase === 'idle') return null;
  return (
    <p role="status" className="flex items-center gap-2 px-1 pb-1 text-xs text-muted">
      {voice.phase === 'starting' && 'Starting the microphone…'}
      {voice.phase === 'recording' && (
        <>
          <span aria-hidden="true" className="h-2 w-2 animate-pulse rounded-full bg-danger" />
          Recording {formatSeconds(voice.elapsed)} / {formatSeconds(maxSeconds)}. Stop to send it, or discard
          it.
        </>
      )}
      {voice.phase === 'transcribing' && (
        <>
          <Spinner small /> Transcribing…
        </>
      )}
    </p>
  );
}

/** The microphone button, or stop and discard while recording. */
export function VoiceButtons({ voice, disabled }: { voice: Voice; disabled: boolean }) {
  if (voice.phase === 'recording')
    return (
      <>
        <Button
          variant="ghost"
          aria-label="Discard recording"
          title="Discard recording"
          onClick={voice.cancel}
          className="px-3"
        >
          ✕
        </Button>
        <Button
          variant="danger"
          aria-label="Stop and send"
          title="Stop recording and send what you said"
          onClick={voice.stop}
          className="px-3"
        >
          ■
        </Button>
      </>
    );
  return (
    <Button
      variant="ghost"
      aria-label="Record a voice message"
      title={
        voiceUnavailableReason() ??
        'Record a voice message: the gateway transcribes it on this machine and sends the text'
      }
      onClick={() => void voice.start()}
      disabled={disabled || voice.phase !== 'idle'}
      busy={voice.phase === 'transcribing'}
      className="px-3"
    >
      🎤
    </Button>
  );
}
