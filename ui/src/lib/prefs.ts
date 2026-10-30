/**
 * The only module allowed to use localStorage (enforced by ESLint). It stores non-sensitive UI
 * preferences (tool-details expansion, last agent picked, theme); never credentials, tokens or content.
 * Storage can throw (private mode, quota), so every access is guarded.
 */
const EXPAND_TOOLS_KEY = 'sharpclaw.expandToolDetails';

export function getExpandToolDetails(): boolean {
  try {
    return localStorage.getItem(EXPAND_TOOLS_KEY) === '1';
  } catch {
    return false;
  }
}

export function setExpandToolDetails(value: boolean) {
  try {
    localStorage.setItem(EXPAND_TOOLS_KEY, value ? '1' : '0');
  } catch {
    // Not persisted; the in-memory value still applies for this page.
  }
}

const LAST_AGENT_KEY = 'sharpclaw.lastAgent';

/** The agent last picked in the Prompts tab (a name, not sensitive). */
export function getLastAgent(): string | null {
  try {
    return localStorage.getItem(LAST_AGENT_KEY);
  } catch {
    return null;
  }
}

export function setLastAgent(name: string) {
  try {
    localStorage.setItem(LAST_AGENT_KEY, name);
  } catch {
    // Not persisted.
  }
}

/** `dark` is the default look; `light` is its light variant; `lcars` is the LCARS theme. */
export type Theme = 'dark' | 'light' | 'lcars';
export const THEMES: readonly Theme[] = ['dark', 'light', 'lcars'];
const THEME_KEY = 'sharpclaw.theme';

/** Dark unless the user picked another theme in the user menu (unknown stored values fall back to dark). */
export function getTheme(): Theme {
  try {
    const stored = localStorage.getItem(THEME_KEY);
    return THEMES.find((t) => t === stored) ?? 'dark';
  } catch {
    return 'dark';
  }
}

export function setTheme(theme: Theme) {
  try {
    localStorage.setItem(THEME_KEY, theme);
  } catch {
    // Not persisted; the theme still applies for this page.
  }
}
