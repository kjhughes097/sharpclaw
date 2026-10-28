/**
 * The only module allowed to use localStorage (enforced by ESLint). It stores one non-sensitive UI
 * preference; never credentials, tokens or content. Storage can throw (private mode, quota), so
 * every access is guarded.
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
