import { useEffect, useState, type FormEvent } from 'react';
import { fetchAppSettings, updateAppSettings } from './api';

interface AppSettingsConfigViewProps {
  onMenuClick: () => void;
}

export function AppSettingsConfigView({ onMenuClick }: AppSettingsConfigViewProps) {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [workspacePath, setWorkspacePath] = useState('');
  const [clearWorkspacePath, setClearWorkspacePath] = useState(false);

  useEffect(() => {
    void load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);

    try {
      const settings = await fetchAppSettings();
      setWorkspacePath(settings.workspacePath);
      setClearWorkspacePath(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  async function handleSave(event: FormEvent) {
    event.preventDefault();
    setSaving(true);
    setError(null);
    setStatus(null);

    try {
      const payload = {
        clearWorkspacePath,
        ...(workspacePath.trim() ? { workspacePath: workspacePath.trim() } : {}),
      };

      const updated = await updateAppSettings(payload);
      setWorkspacePath(updated.workspacePath);
      setClearWorkspacePath(false);
      setStatus('Workspace settings saved. New sessions will use this path for filesystem MCP access.');
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
          <strong>Configure App</strong>
          <span>Manage global runtime settings for SharpClaw.</span>
        </div>
      </div>

      <section className="agent-editor-panel agent-editor-standalone">
        <div className="agent-editor-header">
          <div>
            <h2>Workspace Path</h2>
            <p>Used by filesystem MCP allowed directories and backend runtime integrations.</p>
          </div>
          <div className="agent-editor-actions">
            <button className="secondary-btn" onClick={() => void load()} disabled={loading || saving}>Reload</button>
          </div>
        </div>

        {error && <div className="config-banner error">{error}</div>}
        {status && <div className="config-banner success">{status}</div>}

        {loading ? (
          <div className="config-empty-state">Loading app settings…</div>
        ) : (
          <form className="agent-form" onSubmit={handleSave}>
            <label>
              <span>Workspace Path</span>
              <input
                value={workspacePath}
                onChange={event => setWorkspacePath(event.target.value)}
                placeholder="/workspace"
              />
            </label>

            <label className="agent-checkbox telegram-clear-token">
              <span>Reset workspace path to default on save</span>
              <input
                type="checkbox"
                checked={clearWorkspacePath}
                onChange={event => setClearWorkspacePath(event.target.checked)}
              />
            </label>

            <div className="agent-card-note">
              This path is persisted in the database and used as the primary filesystem workspace root.
            </div>

            <div className="agent-form-actions">
              <button className="new-session-btn agent-add-btn" type="submit" disabled={saving}>
                {saving ? 'Saving…' : 'Save App Settings'}
              </button>
            </div>
          </form>
        )}
      </section>
    </div>
  );
}
