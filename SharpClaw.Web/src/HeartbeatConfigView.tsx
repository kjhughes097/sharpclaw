import { useEffect, useState, type FormEvent } from 'react';
import { fetchHeartbeatSettings, updateHeartbeatSettings } from './api';

interface HeartbeatConfigViewProps {
  onMenuClick: () => void;
}

export function HeartbeatConfigView({ onMenuClick }: HeartbeatConfigViewProps) {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [enabled, setEnabled] = useState(true);
  const [intervalSeconds, setIntervalSeconds] = useState(300);
  const [stuckThresholdSeconds, setStuckThresholdSeconds] = useState(600);

  useEffect(() => {
    void load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);

    try {
      const settings = await fetchHeartbeatSettings();
      setEnabled(settings.enabled);
      setIntervalSeconds(settings.intervalSeconds);
      setStuckThresholdSeconds(settings.stuckThresholdSeconds);
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
      const updated = await updateHeartbeatSettings({
        enabled,
        intervalSeconds,
        stuckThresholdSeconds,
      });
      setEnabled(updated.enabled);
      setIntervalSeconds(updated.intervalSeconds);
      setStuckThresholdSeconds(updated.stuckThresholdSeconds);
      setStatus('Heartbeat settings saved. Changes take effect on the next check cycle.');
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
          <strong>Configure Heartbeat</strong>
          <span>Stuck-session monitor that periodically checks for stalled activity.</span>
        </div>
      </div>

      <section className="agent-editor-panel agent-editor-standalone">
        <div className="agent-editor-header">
          <div>
            <h2>Heartbeat Monitor</h2>
            <p>Periodically checks for sessions holding in-memory resources with no recent activity.</p>
          </div>
          <div className="agent-editor-actions">
            <button className="secondary-btn" onClick={() => void load()} disabled={loading || saving}>Reload</button>
          </div>
        </div>

        {error && <div className="config-banner error">{error}</div>}
        {status && <div className="config-banner success">{status}</div>}

        {loading ? (
          <div className="config-empty-state">Loading heartbeat settings…</div>
        ) : (
          <form className="agent-form" onSubmit={handleSave}>
            <label className="agent-checkbox">
              <span>Enabled</span>
              <input
                type="checkbox"
                checked={enabled}
                onChange={event => setEnabled(event.target.checked)}
              />
            </label>

            <label>
              <span>Check interval (seconds)</span>
              <input
                type="number"
                min={10}
                step={1}
                value={intervalSeconds}
                onChange={event => setIntervalSeconds(Number(event.target.value))}
              />
            </label>

            <label>
              <span>Stuck threshold (seconds)</span>
              <input
                type="number"
                min={10}
                step={1}
                value={stuckThresholdSeconds}
                onChange={event => setStuckThresholdSeconds(Number(event.target.value))}
              />
            </label>

            <div className="agent-card-note">
              Sessions with active runners or streams whose last activity exceeds the stuck threshold will be flagged in the server logs.
            </div>

            <div className="agent-form-actions">
              <button className="new-session-btn agent-add-btn" type="submit" disabled={saving}>
                {saving ? 'Saving…' : 'Save Heartbeat Settings'}
              </button>
            </div>
          </form>
        )}
      </section>
    </div>
  );
}
