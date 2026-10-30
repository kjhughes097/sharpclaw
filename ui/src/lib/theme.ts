import { useSyncExternalStore } from 'react';
import { getTheme, setTheme, type Theme } from './prefs';

/** Menu labels for each theme, in menu order. */
export const THEME_LABELS: Record<Theme, string> = {
  dark: 'Default',
  light: 'Light',
  lcars: 'LCARS',
};

/**
 * Applies a theme to the page: dark is the default (no attribute); any other theme sets
 * `data-theme` on <html>, which switches the CSS tokens and enables the `lcars:` variant.
 */
export function applyTheme(theme: Theme) {
  if (theme === 'dark') delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = theme;
}

// A tiny external store: the shell (which swaps layout for LCARS) and the user menu (which picks
// the theme) must agree on the current theme.
let current: Theme = 'dark';
const listeners = new Set<() => void>();

/** Reads the remembered theme and applies it. Call once before the first render (no flash). */
export function initTheme() {
  current = getTheme();
  applyTheme(current);
}

/** Applies, remembers and broadcasts a theme. */
export function chooseTheme(theme: Theme) {
  setTheme(theme);
  applyTheme(theme);
  current = theme;
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The current theme and a setter that applies and remembers it. */
export function useTheme() {
  const theme = useSyncExternalStore(subscribe, () => current);
  return { theme, choose: chooseTheme };
}
