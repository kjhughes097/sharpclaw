import { useEffect, useRef, useState } from 'react';
import { failReasonMessage } from '../../api/errors';
import type { LiveTurn } from '../../api/live';
import { useNotices, useServices } from '../../app/services';
import { useToast } from '../../app/toast';

/** Shows (once) and dismisses turn-failure notices for this thread from the event stream. */
export function useTurnFailureToasts(threadId: string) {
  const toast = useToast();
  const { notices } = useServices();
  const allNotices = useNotices();
  useEffect(() => {
    for (const notice of allNotices.filter((n) => n.threadId === threadId)) {
      toast(failReasonMessage(notice.code), 'error');
      notices.dismiss(notice.correlationId);
    }
  }, [allNotices, threadId, toast, notices]);
}

/** Text for a polite live region: announces the start and end of a reply, never each delta. */
export function useReplyAnnouncement(liveTurns: LiveTurn[]): string {
  const [announcement, setAnnouncement] = useState('');
  const liveIds = liveTurns.map((t) => t.correlationId).join(',');
  const previousLive = useRef('');
  useEffect(() => {
    const before = new Set(previousLive.current.split(',').filter(Boolean));
    const now = new Set(liveIds.split(',').filter(Boolean));
    if ([...now].some((id) => !before.has(id))) setAnnouncement('Assistant is replying');
    else if ([...before].some((id) => !now.has(id))) setAnnouncement('Reply complete');
    previousLive.current = liveIds;
  }, [liveIds]);
  return announcement;
}
