import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';

const NEAR_BOTTOM_PX = 48;

/**
 * Message-list scrolling: pinned to the bottom while new content arrives, unless the user has
 * scrolled up (then a "jump to latest" button shows); keeps the view steady when older rows are
 * prepended. `contentKey` changes whenever rows or live text change.
 */
export function useThreadScroll(threadId: string, contentKey: string) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const pinnedRef = useRef(true);
  const prependRef = useRef<{ height: number; top: number } | null>(null);
  const [showJump, setShowJump] = useState(false);

  const onScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < NEAR_BOTTOM_PX;
    pinnedRef.current = atBottom;
    if (atBottom) setShowJump(false);
  };

  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    if (prependRef.current) {
      // Older rows were prepended: keep the same content in view.
      el.scrollTop = prependRef.current.top + (el.scrollHeight - prependRef.current.height);
      prependRef.current = null;
      return;
    }
    if (pinnedRef.current) el.scrollTop = el.scrollHeight;
    else setShowJump(true);
  }, [contentKey]);

  useEffect(() => {
    pinnedRef.current = true;
    setShowJump(false);
  }, [threadId]);

  /** Follow new content again (e.g. after sending). */
  const pin = useCallback(() => {
    pinnedRef.current = true;
  }, []);

  return {
    scrollRef,
    onScroll,
    showJump,
    pin,
    jumpToLatest: () => {
      const el = scrollRef.current;
      if (el) el.scrollTop = el.scrollHeight;
      pinnedRef.current = true;
      setShowJump(false);
    },
    /** Call just before older rows are loaded, so the view stays on the same content. */
    beforePrepend: () => {
      const el = scrollRef.current;
      if (el) prependRef.current = { height: el.scrollHeight, top: el.scrollTop };
    },
  };
}
