/**
 * The only module allowed to use localStorage (enforced by ESLint). It stores non-sensitive UI
 * preferences (tool-details expansion, last agent picked); never credentials, tokens or content.
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
