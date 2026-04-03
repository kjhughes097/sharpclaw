import { FormEvent, useEffect, useState } from 'react';
import { fetchBackendSettings, updateBackendSettings } from './api';
import type { BackendSettings } from './types';

interface BackendsConfigViewProps {
  onMenuClick: () => void;
}

interface BackendFormState {
  isEnabled: boolean;
  apiKeyInput: string;
  clearApiKey: boolean;
  hasStoredApiKey: boolean;
  maskedApiKey: string | null;
  requiresApiKey: boolean;
  updatedAt: string | null;
}

function toFormState(setting: BackendSettings): BackendFormState {
  return {
    isEnabled: setting.isEnabled,
    apiKeyInput: '',
    clearApiKey: false,
    hasStoredApiKey: setting.hasApiKey,
    maskedApiKey: setting.maskedApiKey,
    requiresApiKey: setting.requiresApiKey,
    updatedAt: setting.updatedAt,
  };
}

export function BackendsConfigView({ onMenuClick }: BackendsConfigViewProps) {
  const [settings, setSettings] = useState<BackendSettings[]>([]);
  const [forms, setForms] = useState<Record<string, BackendFormState>>({});
  const [loading, setLoading] = useState(true);
  const [savingBackend, setSavingBackend] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    void loadSettings();
  }, []);

  async function loadSettings() {
    setLoading(true);
    setError(null);

    try {
      const result = await fetchBackendSettings();
      setSettings(result);
      setForms(Object.fromEntries(result.map(setting => [setting.backend, toFormState(setting)])));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  function updateForm(backend: string, updater: (current: BackendFormState) => BackendFormState) {
    setForms(prev => {
      const current = prev[backend];
      if (!current) return prev;
      return { ...prev, [backend]: updater(current) };
    });
  }

  async function handleSave(event: FormEvent, backend: string) {
    event.preventDefault();
    const form = forms[backend];
    if (!form) return;

    setSavingBackend(backend);
    setError(null);
    setStatus(null);

    try {
      const payload = {
        isEnabled: form.isEnabled,
        clearApiKey: form.clearApiKey,
        ...(form.apiKeyInput.trim() ? { apiKey: form.apiKeyInput.trim() } : {}),
      };

      const updated = await updateBackendSettings(backend, payload);

      setSettings(prev => prev.map(setting => setting.backend === backend ? updated : setting));
      setForms(prev => ({ ...prev, [backend]: toFormState(updated) }));
      setStatus(`Saved backend settings for ${backend}.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSavingBackend(null);
    }
  }

  return (
    <div className="chat-area">
      <div className="chat-header">
        <button className="menu-btn" onClick={onMenuClick} aria-label="Open menu">☰</button>
        <div className="config-header-copy">
          <strong>Configure Backends</strong>
          <span>Enable providers and store their API keys in the database.</span>
        </div>
      </div>

      {error && <div className="config-banner error">{error}</div>}
      {status && <div className="config-banner success">{status}</div>}

      {loading ? (
        <div className="config-empty-state">Loading backend settings…</div>
      ) : settings.length === 0 ? (
        <div className="config-empty-state">No backend providers are registered.</div>
      ) : (
        <section className="backend-settings-grid">
          {settings.map(setting => {
            const form = forms[setting.backend];
            if (!form) return null;

            const isSaving = savingBackend === setting.backend;

            return (
              <form
                key={setting.backend}
                className="backend-settings-card"
                onSubmit={event => void handleSave(event, setting.backend)}
              >
                <div className="backend-settings-top">
                  <h3>{setting.backend}</h3>
                  <span className={`agent-status-pill ${form.isEnabled ? 'enabled' : 'disabled'}`}>
                    {form.isEnabled ? 'Enabled' : 'Disabled'}
                  </span>
                </div>

                <label className="agent-checkbox backend-enabled-checkbox">
                  <span>Enabled</span>
                  <input
                    type="checkbox"
                    checked={form.isEnabled}
                    onChange={event => updateForm(setting.backend, current => ({ ...current, isEnabled: event.target.checked }))}
                  />
                </label>

                <div className="telegram-token-state backend-token-state">
                  <span className="telegram-token-state-label">Stored API Key</span>
                  <span>{form.hasStoredApiKey ? (form.maskedApiKey ?? 'Configured') : 'Not configured'}</span>
                </div>

                <label>
                  <span>New API Key</span>
                  <input
                    value={form.apiKeyInput}
                    onChange={event => updateForm(setting.backend, current => ({ ...current, apiKeyInput: event.target.value }))}
                    placeholder="Paste a key to set or rotate"
                  />
                </label>

                <label className="agent-checkbox telegram-clear-token">
                  <span>Clear Stored API Key</span>
                  <input
                    type="checkbox"
                    checked={form.clearApiKey}
                    onChange={event => updateForm(setting.backend, current => ({ ...current, clearApiKey: event.target.checked }))}
                  />
                </label>

                <small className="field-hint">
                  {form.requiresApiKey
                    ? 'This backend requires an API key when enabled.'
                    : 'This backend can run without a stored API key.'}
                </small>

                {form.updatedAt && (
                  <small className="field-hint">Last updated: {new Date(form.updatedAt).toLocaleString()}</small>
                )}

                <div className="agent-form-actions backend-settings-actions">
                  <button className="new-session-btn agent-add-btn" type="submit" disabled={isSaving}>
                    {isSaving ? 'Saving…' : 'Save'}
                  </button>
                </div>
              </form>
            );
          })}
        </section>
      )}
    </div>
  );
}
