import { forwardRef, useEffect, type ElementType, type ReactNode } from 'react';

/**
 * Stand-in for `react-remove-scroll`, aliased in vite.config.ts. Radix dialogs, menus and selects
 * use it to lock page scrolling, but the original injects a `<style>` element that the gateway's
 * CSP (`style-src 'self'`) blocks. This version locks scrolling through the CSSOM, which the CSP
 * allows. Focus trapping and outside-click handling stay with Radix.
 */
type Props = {
  as?: ElementType;
  enabled?: boolean;
  children?: ReactNode;
  // Accepted for API compatibility; not needed by this implementation.
  allowPinchZoom?: boolean;
  shards?: unknown[];
  [key: string]: unknown;
};

const IGNORED = new Set([
  'noIsolation',
  'inert',
  'removeScrollBar',
  'gapMode',
  'lockRef',
  'sideCar',
  'forwardProps',
  'noRelative',
]);

let locks = 0;
let previousOverflow = '';

export const RemoveScroll = forwardRef<HTMLElement, Props>(function RemoveScroll(
  { as, enabled = true, children, allowPinchZoom: _p, shards: _s, ...rest },
  ref,
) {
  const Component = (as ?? 'div') as ElementType;
  useEffect(() => {
    if (!enabled) return;
    if (locks++ === 0) {
      previousOverflow = document.body.style.overflow;
      document.body.style.overflow = 'hidden';
    }
    return () => {
      if (--locks === 0) document.body.style.overflow = previousOverflow;
    };
  }, [enabled]);

  // Radix only passes props it expects us to forward to its Slot.
  const forwarded = Object.fromEntries(Object.entries(rest).filter(([key]) => !IGNORED.has(key)));
  return (
    <Component ref={ref} {...forwarded}>
      {children}
    </Component>
  );
});

export default RemoveScroll;
