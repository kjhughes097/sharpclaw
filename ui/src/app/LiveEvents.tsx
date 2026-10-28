import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { applyEvent } from '../api/eventEffects';
import { useServices } from './services';

/** Connects the tab's event stream to the query cache and live buffers. Renders nothing. */
export function LiveEvents() {
  const queryClient = useQueryClient();
  const { stream, live, notices } = useServices();

  useEffect(
    () =>
      stream.subscribe((event) =>
        applyEvent(event, { queryClient, live, onTurnError: (notice) => notices.push(notice) }),
      ),
    [stream, live, notices, queryClient],
  );
  return null;
}
