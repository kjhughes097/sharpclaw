import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { toHaveNoViolations } from 'jest-axe';
import { afterAll, afterEach, beforeAll, expect } from 'vitest';
import { setCsrfToken, setUnauthorizedHandler } from '../src/api/client';
import { resetServerTime } from '../src/lib/time';
import { server } from './msw';

expect.extend(toHaveNoViolations);

// Secure-context APIs are unavailable over plain HTTP on the LAN; the UI must never rely on
// them, so the whole suite runs without them (3.3 spec section 11).
Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });
if (globalThis.crypto) Object.defineProperty(globalThis.crypto, 'randomUUID', { value: undefined, configurable: true });

// jsdom lacks these; Radix and the scroll logic use them.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver;
Element.prototype.scrollIntoView ??= function scrollIntoView() {};
Element.prototype.hasPointerCapture ??= () => false;
Element.prototype.releasePointerCapture ??= () => {};
document.execCommand ??= () => false;
window.matchMedia ??= (query: string) =>
  ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
  }) as MediaQueryList;

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => {
  cleanup();
  server.resetHandlers();
  setCsrfToken(null);
  setUnauthorizedHandler(null);
  resetServerTime();
  document.title = '';
});
afterAll(() => server.close());
