import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { EventType } from '../../api/events';
import { keys } from '../../api/queryKeys';
import { useServices } from '../../app/services';

/** Events that change the message feed; the last two also change usage. */
const FEED_EVENTS = new Set<EventType>(['message.created', 'message.completed', 'error']);

/** Several events per turn arrive close together: refetch at most once per this window. */
export const LIVE_DEBOUNCE_MS = 1000;

/**
 * Keeps the activity and usage queries live from the event stream (3.3.1 spec section 5.3), with
 * a pause: while paused nothing is refetched and relevant events are only counted; resuming
 * refetches. Debounced, so a burst of events causes one refetch.
 */
export function useLiveActivity() {
  const { stream } = useServices();
  const queryClient = useQueryClient();
  const [paused, setPaused] = useState(false);
  const [missed, setMissed] = useState(0);
  const pausedRef = useRef(paused);

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    let usageChanged = false;
    const flush = () => {
      timer = undefined;
      void queryClient.invalidateQueries({ queryKey: keys.activity });
      if (usageChanged) void queryClient.invalidateQueries({ queryKey: keys.usage });
      usageChanged = false;
    };
    const unsubscribe = stream.subscribe((event) => {
      if (!FEED_EVENTS.has(event.type)) return;
      if (pausedRef.current) {
        setMissed((n) => n + 1);
        return;
      }
      if (event.type !== 'message.created') usageChanged = true;
      timer ??= setTimeout(flush, LIVE_DEBOUNCE_MS);
    });
    return () => {
      unsubscribe();
      if (timer) clearTimeout(timer);
    };
  }, [stream, queryClient]);

  const pause = useCallback(() => {
    pausedRef.current = true;
    setPaused(true);
  }, []);

  const resume = useCallback(() => {
    pausedRef.current = false;
    setPaused(false);
    setMissed(0);
    void queryClient.invalidateQueries({ queryKey: keys.activity });
    void queryClient.invalidateQueries({ queryKey: keys.usage });
  }, [queryClient]);

  return { paused, missed, pause, resume };
}
