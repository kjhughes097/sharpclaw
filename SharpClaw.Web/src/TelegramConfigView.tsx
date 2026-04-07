import { useEffect, useState, type FormEvent } from 'react';
import { createTelegramWorkerToken, fetchTelegramSettings, updateTelegramSettings } from './api';

interface TelegramConfigViewProps {
  onMenuClick: () => void;
}

function parseUserIdList(value: string): number[] {
  if (!value.trim()) return [];

  const seen = new Set<number>();
  const parsed: number[] = [];

  for (const part of value.split(',')) {
    const trimmed = part.trim();
    if (!trimmed) continue;

    const id = Number(trimmed);
    if (!Number.isInteger(id) || id <= 0)
      throw new Error(`Invalid Telegram user ID: ${trimmed}`);

    if (seen.has(id)) continue;
    seen.add(id);
    parsed.push(id);
  }

  return parsed;
}

function parseUsernameList(value: string): string[] {
  if (!value.trim()) return [];

  const seen = new Set<string>();
  const parsed: string[] = [];

  for (const part of value.split(',')) {
    const normalized = part.trim().replace(/^@+/, '');
    if (!normalized) continue;

    const key = normalized.toLowerCase();
    if (seen.has(key)) continue;

    seen.add(key);
    parsed.push(normalized);
  }

  return parsed;
}

export function TelegramConfigView({ onMenuClick }: TelegramConfigViewProps) {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  const [isEnabled, setIsEnabled] = useState(false);
  const [hasStoredToken, setHasStoredToken] = useState(false);
  const [maskedToken, setMaskedToken] = useState<string | null>(null);
  const [tokenInput, setTokenInput] = useState('');
  const [clearToken, setClearToken] = useState(false);
  const [allowedUserIdsText, setAllowedUserIdsText] = useState('');
  const [allowedUsernamesText, setAllowedUsernamesText] = useState('');
  const [mappingStorePath, setMappingStorePath] = useState('');
  const [clearMappingStorePath, setClearMappingStorePath] = useState(false);
  const [workerApiToken, setWorkerApiToken] = useState<string | null>(null);
  const [workerApiTokenExpiresAt, setWorkerApiTokenExpiresAt] = useState<string | null>(null);
  const [generatingWorkerToken, setGeneratingWorkerToken] = useState(false);

  useEffect(() => {
    void load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);

    try {
      const settings = await fetchTelegramSettings();
      setIsEnabled(settings.isEnabled);
      setHasStoredToken(settings.hasBotToken);
      setMaskedToken(settings.maskedBotToken ?? null);
      setAllowedUserIdsText(settings.allowedUserIds.join(', '));
      setAllowedUsernamesText(settings.allowedUsernames.join(', '));
      setMappingStorePath(settings.mappingStorePath);
      setTokenInput('');
      setClearToken(false);
      setClearMappingStorePath(false);
      setWorkerApiToken(null);
      setWorkerApiTokenExpiresAt(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  async function handleGenerateWorkerToken() {
    setGeneratingWorkerToken(true);
    setError(null);
    setStatus(null);

    try {
      const result = await createTelegramWorkerToken();
      setWorkerApiToken(result.token);
      setWorkerApiTokenExpiresAt(result.expiresAt);
      setStatus('Generated new Telegram worker API token. Copy it now and place it in SHARPCLAW_API_TOKEN.');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setGeneratingWorkerToken(false);
    }
  }

  async function handleCopyWorkerToken() {
    if (!workerApiToken)
      return;

    try {
      await navigator.clipboard.writeText(workerApiToken);
      setStatus('Telegram worker API token copied to clipboard.');
    } catch {
      setError('Failed to copy token to clipboard. Copy it manually from the field below.');
    }
  }

  async function handleSave(event: FormEvent) {
    event.preventDefault();
    setSaving(true);
    setError(null);
    setStatus(null);

    try {
      const allowedUserIds = parseUserIdList(allowedUserIdsText);
      const allowedUsernames = parseUsernameList(allowedUsernamesText);

      const payload = {
        isEnabled,
        allowedUserIds,
        allowedUsernames,
        clearBotToken: clearToken,
        clearMappingStorePath,
        ...(tokenInput.trim() ? { botToken: tokenInput.trim() } : {}),
        ...(mappingStorePath.trim() ? { mappingStorePath: mappingStorePath.trim() } : {}),
      };

      const updated = await updateTelegramSettings(payload);
      setHasStoredToken(updated.hasBotToken);
      setMaskedToken(updated.maskedBotToken ?? null);
      setAllowedUserIdsText(updated.allowedUserIds.join(', '));
      setAllowedUsernamesText(updated.allowedUsernames.join(', '));
      setMappingStorePath(updated.mappingStorePath);
      setTokenInput('');
      setClearToken(false);
      setClearMappingStorePath(false);
      setStatus('Telegram settings saved. Restart the Telegram service to apply token changes.');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="chat-area">
      <div className="chat-header">
        <button className="menu-btn" onClick={onMenuClick} aria-label="Open menu">☰</button>
        <div className="config-header-copy">
          <strong>Configure Telegram</strong>
          <span>Enable integration, set bot credentials, and manage sender allow lists.</span>
        </div>
      </div>

      <section className="agent-editor-panel agent-editor-standalone">
        <div className="agent-editor-header">
          <div>
            <h2>Telegram Integration</h2>
            <p>Use comma-separated values for allow lists. Changes to the token require restarting the Telegram worker.</p>
          </div>
          <div className="agent-editor-actions">
            <button className="secondary-btn" onClick={() => void load()} disabled={loading || saving}>Reload</button>
          </div>
        </div>

        {error && <div className="config-banner error">{error}</div>}
        {status && <div className="config-banner success">{status}</div>}

        {loading ? (
          <div className="config-empty-state">Loading Telegram settings…</div>
        ) : (
          <form className="agent-form" onSubmit={handleSave}>
            <div className="agent-form-row two-up">
              <label className="agent-checkbox">
                <span>Enabled</span>
                <input
                  type="checkbox"
                  checked={isEnabled}
                  onChange={event => setIsEnabled(event.target.checked)}
                />
              </label>
              <div className="telegram-token-state">
                <span className="telegram-token-state-label">Stored Bot Token</span>
                <span className={`agent-status-pill ${hasStoredToken ? 'enabled' : 'disabled'}`}>
                  {hasStoredToken ? (maskedToken ?? 'Present') : 'Not set'}
                </span>
              </div>
            </div>

            <label>
              <span>Bot Token (optional update)</span>
              <input
                value={tokenInput}
                onChange={event => setTokenInput(event.target.value)}
                placeholder="123456789:AA..."
                autoComplete="off"
              />
            </label>

            <label className="agent-checkbox telegram-clear-token">
              <span>Clear stored token on save</span>
              <input
                type="checkbox"
                checked={clearToken}
                onChange={event => setClearToken(event.target.checked)}
              />
            </label>

            <label>
              <span>Allowed User IDs</span>
              <input
                value={allowedUserIdsText}
                onChange={event => setAllowedUserIdsText(event.target.value)}
                placeholder="12345678, 87654321"
              />
            </label>

            <label>
              <span>Allowed Usernames</span>
              <input
                value={allowedUsernamesText}
                onChange={event => setAllowedUsernamesText(event.target.value)}
                placeholder="alice, @bob"
              />
            </label>

            <label>
              <span>Mapping Store Path</span>
              <input
                value={mappingStorePath}
                onChange={event => setMappingStorePath(event.target.value)}
                placeholder="/var/lib/sharpclaw/telegram-session-mappings.json"
              />
            </label>

            <label className="agent-checkbox telegram-clear-token">
              <span>Reset mapping path to default on save</span>
              <input
                type="checkbox"
                checked={clearMappingStorePath}
                onChange={event => setClearMappingStorePath(event.target.checked)}
              />
            </label>

            <div className="agent-card-note">
              Leave both allow lists empty to permit any sender. If either list is populated, only matching users can interact with the bot.
            </div>

            <div className="agent-card-note">
              Generate a dedicated API token for the Telegram worker and set it as SHARPCLAW_API_TOKEN in the worker environment.
            </div>

            <div className="agent-form-actions">
              <button
                type="button"
                className="secondary-btn"
                onClick={() => void handleGenerateWorkerToken()}
                disabled={saving || generatingWorkerToken}
              >
                {generatingWorkerToken ? 'Generating…' : 'Generate Worker API Token'}
              </button>
              <button
                type="button"
                className="secondary-btn"
                onClick={() => void handleCopyWorkerToken()}
                disabled={!workerApiToken}
              >
                Copy Token
              </button>
            </div>

            {workerApiToken && (
              <>
                <label>
                  <span>Generated Worker API Token</span>
                  <input
                    value={workerApiToken}
                    readOnly
                    onFocus={event => event.currentTarget.select()}
                  />
                </label>
                <div className="agent-card-note">
                  Expires at: {workerApiTokenExpiresAt ? new Date(workerApiTokenExpiresAt).toLocaleString() : 'unknown'}
                </div>
              </>
            )}

            <div className="agent-form-actions">
              <button className="new-session-btn agent-add-btn" type="submit" disabled={saving}>
                {saving ? 'Saving…' : 'Save Telegram Settings'}
              </button>
            </div>
          </form>
        )}
      </section>
    </div>
  );
}
