import { useState } from 'react';
import { getTheme, setTheme, type Theme } from './prefs';

/** Applies a theme to the page: dark is the default, light sets `data-theme="light"` on <html>. */
export function applyTheme(theme: Theme) {
  if (theme === 'light') document.documentElement.dataset.theme = 'light';
  else delete document.documentElement.dataset.theme;
}

/** The current theme and a toggle that applies and remembers it. */
export function useTheme() {
  const [theme, setState] = useState<Theme>(getTheme);
  const toggle = () => {
    const next: Theme = theme === 'dark' ? 'light' : 'dark';
    setTheme(next);
    applyTheme(next);
    setState(next);
  };
  return { theme, toggle };
}
