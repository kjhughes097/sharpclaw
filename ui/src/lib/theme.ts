import { useState } from 'react';
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

/** The current theme and a setter that applies and remembers it. */
export function useTheme() {
  const [theme, setState] = useState<Theme>(getTheme);
  const choose = (next: Theme) => {
    setTheme(next);
    applyTheme(next);
    setState(next);
  };
  return { theme, choose };
}
