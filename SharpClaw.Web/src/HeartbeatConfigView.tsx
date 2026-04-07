import { useEffect, useState, type FormEvent } from 'react';
import { fetchHeartbeatSettings, updateHeartbeatSettings, fetchHeartbeatDiagnostics, cleanupStuckSession, cleanupAllStuckSessions, type HeartbeatDiagnostics } from './api';

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
  const [autoCleanupEnabled, setAutoCleanupEnabled] = useState(true);
  const [autoCleanupThresholdSeconds, setAutoCleanupThresholdSeconds] = useState(1200);

  const [diagnostics, setDiagnostics] = useState<HeartbeatDiagnostics | null>(null);
  const [diagLoading, setDiagLoading] = useState(false);
  const [diagError, setDiagError] = useState<string | null>(null);
  const [cleaningUp, setCleaningUp] = useState<string | null>(null);

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
      setAutoCleanupEnabled(settings.autoCleanupEnabled);
      setAutoCleanupThresholdSeconds(settings.autoCleanupThresholdSeconds);
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
        autoCleanupEnabled,
        autoCleanupThresholdSeconds,
      });
      setEnabled(updated.enabled);
      setIntervalSeconds(updated.intervalSeconds);
      setStuckThresholdSeconds(updated.stuckThresholdSeconds);
      setAutoCleanupEnabled(updated.autoCleanupEnabled);
      setAutoCleanupThresholdSeconds(updated.autoCleanupThresholdSeconds);
      setStatus('Heartbeat settings saved. Changes take effect on the next check cycle.');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  async function loadDiagnostics() {
    setDiagLoading(true);
    setDiagError(null);

    try {
      const report = await fetchHeartbeatDiagnostics();
      setDiagnostics(report);
    } catch (err) {
      setDiagError(err instanceof Error ? err.message : String(err));
    } finally {
      setDiagLoading(false);
    }
  }

  async function handleCleanupSession(sessionId: string) {
    setCleaningUp(sessionId);
    setDiagError(null);

    try {
      await cleanupStuckSession(sessionId);
      await loadDiagnostics();
    } catch (err) {
      setDiagError(err instanceof Error ? err.message : String(err));
    } finally {
      setCleaningUp(null);
    }
  }

  async function handleCleanupAll() {
    if (!diagnostics?.stuckSessions.length) return;

    setCleaningUp('all');
    setDiagError(null);

    try {
      await cleanupAllStuckSessions();
      await loadDiagnostics();
    } catch (err) {
      setDiagError(err instanceof Error ? err.message : String(err));
    } finally {
      setCleaningUp(null);
    }
  }

  function formatIdle(seconds: number): string {
    if (seconds < 60) return `${Math.round(seconds)}s`;
    if (seconds < 3600) return `${(seconds / 60).toFixed(1)}m`;
    return `${(seconds / 3600).toFixed(1)}h`;
  }

  return (
    <div className="chat-area">
      <div className="chat-header">
        <button className="menu-btn" onClick={onMenuClick} aria-label="Open menu">☰</button>
        <div className="config-header-copy">
          <strong>Configure Heartbeat</strong>
          <span>Stuck-session monitor with automatic and manual cleanup.</span>
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

            <label className="agent-checkbox">
              <span>Auto-cleanup enabled</span>
              <input
                type="checkbox"
                checked={autoCleanupEnabled}
                onChange={event => setAutoCleanupEnabled(event.target.checked)}
              />
            </label>

            <label>
              <span>Auto-cleanup threshold (seconds)</span>
              <input
                type="number"
                min={10}
                step={1}
                value={autoCleanupThresholdSeconds}
                onChange={event => setAutoCleanupThresholdSeconds(Number(event.target.value))}
              />
            </label>

            <div className="agent-card-note">
              Sessions exceeding the stuck threshold are flagged in server logs. When auto-cleanup is enabled,
              sessions idle beyond the auto-cleanup threshold are automatically cleaned up — their runners and
              streams are disposed, but conversation history is preserved.
            </div>

            <div className="agent-form-actions">
              <button className="new-session-btn agent-add-btn" type="submit" disabled={saving}>
                {saving ? 'Saving…' : 'Save Heartbeat Settings'}
              </button>
            </div>
          </form>
        )}
      </section>

      <section className="agent-editor-panel agent-editor-standalone">
        <div className="agent-editor-header">
          <div>
            <h2>Diagnostics</h2>
            <p>View active sessions and manually clean up stuck ones.</p>
          </div>
          <div className="agent-editor-actions">
            <button className="secondary-btn" onClick={() => void loadDiagnostics()} disabled={diagLoading}>
              {diagLoading ? 'Checking…' : 'Run Check'}
            </button>
          </div>
        </div>

        {diagError && <div className="config-banner error">{diagError}</div>}

        {diagnostics && (
          <div className="agent-form">
            <div className="agent-card-note">
              {diagnostics.activeRunnerCount} active runner(s), {diagnostics.activeStreamCount} active stream(s), {diagnostics.stuckSessions.length} stuck session(s).
              Checked at {new Date(diagnostics.checkedAt).toLocaleString()}.
            </div>

            {diagnostics.stuckSessions.length > 0 && (
              <>
                <div className="agent-form-actions">
                  <button
                    className="secondary-btn"
                    onClick={() => void handleCleanupAll()}
                    disabled={cleaningUp !== null}
                  >
                    {cleaningUp === 'all' ? 'Cleaning up…' : `Clean Up All (${diagnostics.stuckSessions.length})`}
                  </button>
                </div>

                <table className="heartbeat-diagnostics-table">
                  <thead>
                    <tr>
                      <th>Session</th>
                      <th>Agent</th>
                      <th>Idle</th>
                      <th>Runner</th>
                      <th>Streams</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    {diagnostics.stuckSessions.map(s => (
                      <tr key={s.sessionId}>
                        <td title={s.sessionId}>{s.sessionId.slice(0, 8)}…</td>
                        <td>{s.agentSlug}</td>
                        <td>{formatIdle(s.idleSeconds)}</td>
                        <td>{s.hasRunner ? '✓' : '—'}</td>
                        <td>{s.hasStreams ? '✓' : '—'}</td>
                        <td>
                          <button
                            className="secondary-btn"
                            onClick={() => void handleCleanupSession(s.sessionId)}
                            disabled={cleaningUp !== null}
                          >
                            {cleaningUp === s.sessionId ? 'Cleaning…' : 'Clean Up'}
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </>
            )}

            {diagnostics.stuckSessions.length === 0 && (
              <div className="config-empty-state">No stuck sessions detected. ✓</div>
            )}
          </div>
        )}

        {!diagnostics && !diagLoading && (
          <div className="config-empty-state">Click "Run Check" to inspect active sessions.</div>
        )}
      </section>
    </div>
  );
}
