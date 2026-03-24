import { useEffect, useMemo, useState } from 'react';
import { createAgent, deleteAgent, fetchAgents, setAgentEnabled, updateAgent } from './api';
import type { AgentDefinition, AgentUpsertRequest } from './types';

interface AgentConfigViewProps {
  onMenuClick: () => void;
}

interface PermissionRow {
  pattern: string;
  value: string;
}

type BackendKind = 'anthropic' | 'copilot';

const DEFAULT_ANTHROPIC_MODEL = 'claude-haiku-4-5-20251001';
const DEFAULT_COPILOT_MODEL = 'gpt-5.4';

const PERMISSION_TEMPLATES: Record<string, Record<string, string>> = {
  'workspace-editor': {
    read_file: 'auto_approve',
    list_directory: 'auto_approve',
    list_allowed_directories: 'auto_approve',
    search_files: 'auto_approve',
    get_file_info: 'auto_approve',
    write_file: 'ask',
    create_directory: 'ask',
    '*': 'ask',
  },
  'read-only-filesystem': {
    read_file: 'auto_approve',
    list_directory: 'auto_approve',
    list_allowed_directories: 'auto_approve',
    search_files: 'auto_approve',
    get_file_info: 'auto_approve',
    '*': 'ask',
  },
  'ask-all': {
    '*': 'ask',
  },
  'deny-all': {
    '*': 'deny',
  },
};

const BACKEND_DEFAULT_TEMPLATE: Record<BackendKind, keyof typeof PERMISSION_TEMPLATES> = {
  anthropic: 'workspace-editor',
  copilot: 'read-only-filesystem',
};

function defaultModelForBackend(backend: string): string {
  return backend === 'copilot' ? DEFAULT_COPILOT_MODEL : DEFAULT_ANTHROPIC_MODEL;
}

function defaultPermissionsForBackend(backend: string): Record<string, string> {
  const key = BACKEND_DEFAULT_TEMPLATE[(backend === 'copilot' ? 'copilot' : 'anthropic') as BackendKind];
  return PERMISSION_TEMPLATES[key];
}

function policiesEqual(left: Record<string, string>, right: Record<string, string>): boolean {
  const leftEntries = Object.entries(left).sort(([a], [b]) => a.localeCompare(b));
  const rightEntries = Object.entries(right).sort(([a], [b]) => a.localeCompare(b));

  if (leftEntries.length !== rightEntries.length) return false;

  return leftEntries.every(([key, value], index) => {
    const [otherKey, otherValue] = rightEntries[index];
    return key === otherKey && value === otherValue;
  });
}

function blankAgent(): AgentUpsertRequest {
  return {
    file: '',
    name: '',
    description: '',
    backend: 'anthropic',
    model: DEFAULT_ANTHROPIC_MODEL,
    mcpServers: [],
    permissionPolicy: { ...PERMISSION_TEMPLATES['workspace-editor'] },
    systemPrompt: '',
    isEnabled: true,
  };
}

function toForm(agent: AgentDefinition): AgentUpsertRequest {
  return {
    file: agent.file,
    name: agent.name,
    description: agent.description,
    backend: agent.backend,
    model: agent.model,
    mcpServers: [...agent.mcpServers],
    permissionPolicy: { ...agent.permissionPolicy },
    systemPrompt: agent.systemPrompt,
    isEnabled: agent.isEnabled,
  };
}

function toPermissionRows(policy: Record<string, string>): PermissionRow[] {
  const entries = Object.entries(policy);
  return entries.length > 0
    ? entries.map(([pattern, value]) => ({ pattern, value }))
    : [{ pattern: '*', value: 'ask' }];
}

function normalizeList(input: string): string[] {
  return input
    .split(/[,\n]/)
    .map(item => item.trim())
    .filter(Boolean);
}

function normalizePermissions(rows: PermissionRow[]): Record<string, string> {
  return rows.reduce<Record<string, string>>((acc, row) => {
    const pattern = row.pattern.trim();
    const value = row.value.trim().toLowerCase();
    if (pattern && value) acc[pattern] = value;
    return acc;
  }, {});
}

export function AgentConfigView({ onMenuClick }: AgentConfigViewProps) {
  const [agents, setAgents] = useState<AgentDefinition[]>([]);
  const [selectedFile, setSelectedFile] = useState<string | null>(null);
  const [form, setForm] = useState<AgentUpsertRequest>(blankAgent);
  const [permissionRows, setPermissionRows] = useState<PermissionRow[]>(toPermissionRows(blankAgent().permissionPolicy));
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<AgentDefinition | null>(null);
  const [deleteConfirmation, setDeleteConfirmation] = useState('');
  const [purgeLinkedSessions, setPurgeLinkedSessions] = useState(false);

  const selectedAgent = useMemo(
    () => agents.find(agent => agent.file === selectedFile) ?? null,
    [agents, selectedFile],
  );

  useEffect(() => {
    void loadAgents();
  }, []);

  useEffect(() => {
    if (!selectedAgent) return;
    setForm(toForm(selectedAgent));
    setPermissionRows(toPermissionRows(selectedAgent.permissionPolicy));
  }, [selectedAgent]);

  async function loadAgents(preferredSelection?: string | null) {
    setLoading(true);
    setError(null);
    try {
      const result = await fetchAgents();
      setAgents(result);

      const nextSelection = preferredSelection
        ?? (selectedFile && result.some(agent => agent.file === selectedFile) ? selectedFile : result[0]?.file ?? null);

      setSelectedFile(nextSelection);

      if (!nextSelection) {
        setForm(blankAgent());
        setPermissionRows(toPermissionRows(blankAgent().permissionPolicy));
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  function beginCreate() {
    setSelectedFile(null);
    setForm(blankAgent());
    setPermissionRows(toPermissionRows(blankAgent().permissionPolicy));
    setDeleteTarget(null);
    setDeleteConfirmation('');
    setPurgeLinkedSessions(false);
    setStatus(null);
    setError(null);
  }

  function beginEdit(agent: AgentDefinition) {
    setSelectedFile(agent.file);
    setForm(toForm(agent));
    setPermissionRows(toPermissionRows(agent.permissionPolicy));
    setDeleteTarget(null);
    setDeleteConfirmation('');
    setPurgeLinkedSessions(false);
    setStatus(null);
    setError(null);
  }

  function applyPermissionTemplate(templateName: keyof typeof PERMISSION_TEMPLATES) {
    setPermissionRows(toPermissionRows(PERMISSION_TEMPLATES[templateName]));
    setStatus(`Applied ${templateName.replace(/-/g, ' ')} permissions.`);
    setError(null);
  }

  function handleBackendChange(nextBackend: string) {
    const currentPolicy = normalizePermissions(permissionRows);
    const shouldResetPermissions =
      Object.keys(currentPolicy).length === 0 ||
      policiesEqual(currentPolicy, defaultPermissionsForBackend(form.backend));

    const currentModel = form.model.trim();
    const shouldResetModel = !currentModel || currentModel === defaultModelForBackend(form.backend);

    setForm(prev => ({
      ...prev,
      backend: nextBackend,
      model: shouldResetModel ? defaultModelForBackend(nextBackend) : prev.model,
    }));

    if (shouldResetPermissions) {
      setPermissionRows(toPermissionRows(defaultPermissionsForBackend(nextBackend)));
      setStatus(`Applied ${nextBackend} defaults.`);
      setError(null);
    }
  }

  function updatePermissionRow(index: number, field: keyof PermissionRow, value: string) {
    setPermissionRows(prev => prev.map((row, rowIndex) => rowIndex === index ? { ...row, [field]: value } : row));
  }

  function addPermissionRow() {
    setPermissionRows(prev => [...prev, { pattern: '', value: 'ask' }]);
  }

  function removePermissionRow(index: number) {
    setPermissionRows(prev => prev.length === 1 ? prev : prev.filter((_, rowIndex) => rowIndex !== index));
  }

  async function handleSave(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaving(true);
    setError(null);
    setStatus(null);

    const payload: AgentUpsertRequest = {
      ...form,
      file: form.file.trim(),
      name: form.name.trim(),
      description: form.description.trim(),
      backend: form.backend.trim().toLowerCase(),
      model: form.model.trim(),
      mcpServers: normalizeList(form.mcpServers.join('\n')),
      permissionPolicy: normalizePermissions(permissionRows),
      systemPrompt: form.systemPrompt.trim(),
    };

    try {
      const saved = selectedAgent
        ? await updateAgent(selectedAgent.file, payload)
        : await createAgent(payload);

      setStatus(selectedAgent ? 'Agent updated.' : 'Agent created.');
      await loadAgents(saved.file);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  async function handleToggle(agent: AgentDefinition) {
    setError(null);
    setStatus(null);
    try {
      await setAgentEnabled(agent.file, !agent.isEnabled);
      setStatus(`${agent.name} ${agent.isEnabled ? 'disabled' : 'enabled'}.`);
      await loadAgents(agent.file);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleDelete() {
    if (!deleteTarget) return;

    setSaving(true);
    setError(null);
    setStatus(null);

    try {
      const result = await deleteAgent(deleteTarget.file, purgeLinkedSessions);
      setStatus(result.deletedSessions > 0
        ? `Deleted ${deleteTarget.name} and purged ${result.deletedSessions} linked session(s).`
        : `Deleted ${deleteTarget.name}.`);
      setDeleteTarget(null);
      setDeleteConfirmation('');
      setPurgeLinkedSessions(false);
      await loadAgents();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  const mcpText = form.mcpServers.join('\n');
  const isEditing = selectedAgent !== null;

  return (
    <div className="chat-area">
      <div className="chat-header">
        <button className="menu-btn" onClick={onMenuClick} aria-label="Open menu">☰</button>
        <div className="config-header-copy">
          <strong>Configure Agents</strong>
          <span>Manage stored agent definitions and availability.</span>
        </div>
      </div>

      <div className="config-layout">
        <section className="agent-catalog">
          <div className="agent-catalog-header">
            <div>
              <h2>Agents</h2>
              <p>{agents.length} defined</p>
            </div>
            <button className="new-session-btn agent-add-btn" onClick={beginCreate}>+ Add Agent</button>
          </div>

          {loading ? (
            <div className="config-empty-state">Loading agents…</div>
          ) : agents.length === 0 ? (
            <div className="config-empty-state">No agents are defined yet.</div>
          ) : (
            <div className="agent-card-list">
              {agents.map(agent => (
                <article
                  key={agent.file}
                  className={`agent-card${selectedFile === agent.file ? ' selected' : ''}`}
                  onClick={() => beginEdit(agent)}
                >
                  <div className="agent-card-top">
                    <div>
                      <h3>{agent.name}</h3>
                      <div className="agent-card-file">{agent.file}</div>
                    </div>
                    <span className={`agent-status-pill ${agent.isEnabled ? 'enabled' : 'disabled'}`}>
                      {agent.isEnabled ? 'Enabled' : 'Disabled'}
                    </span>
                  </div>

                  <p className="agent-card-description">{agent.description}</p>

                  <div className="agent-meta-grid">
                    <span>{agent.backend}</span>
                    <span>{agent.model || 'Default model'}</span>
                    <span>{agent.mcpServers.length} MCP</span>
                    <span>{Object.keys(agent.permissionPolicy).length} permission rule{Object.keys(agent.permissionPolicy).length === 1 ? '' : 's'}</span>
                    <span>{agent.sessionCount} linked session{agent.sessionCount === 1 ? '' : 's'}</span>
                  </div>

                  <div className="agent-chip-row">
                    {agent.mcpServers.length > 0 ? agent.mcpServers.map(server => (
                      <span key={server} className="agent-chip">{server}</span>
                    )) : <span className="agent-chip muted">No MCP servers</span>}
                  </div>

                  <div className="agent-chip-row permissions">
                    {Object.entries(agent.permissionPolicy).slice(0, 4).map(([pattern, value]) => (
                      <span key={`${pattern}:${value}`} className="agent-chip permission">{pattern}: {value}</span>
                    ))}
                    {Object.keys(agent.permissionPolicy).length > 4 && (
                      <span className="agent-chip muted">+{Object.keys(agent.permissionPolicy).length - 4} more</span>
                    )}
                  </div>

                  <div className="agent-card-actions" onClick={event => event.stopPropagation()}>
                    <button className="secondary-btn" onClick={() => beginEdit(agent)}>Edit</button>
                    <button className="secondary-btn" onClick={() => void handleToggle(agent)}>
                      {agent.isEnabled ? 'Disable' : 'Enable'}
                    </button>
                    <button className="danger-btn" onClick={() => { setDeleteTarget(agent); setDeleteConfirmation(''); setPurgeLinkedSessions(false); }}>Delete</button>
                  </div>
                </article>
              ))}
            </div>
          )}
        </section>

        <section className="agent-editor-panel">
          <div className="agent-editor-header">
            <div>
              <h2>{isEditing ? 'Edit Agent' : 'New Agent'}</h2>
              <p>{isEditing ? 'Update the stored definition for this agent.' : 'Create a new agent definition in the database.'}</p>
            </div>
            {isEditing && <button className="secondary-btn" onClick={beginCreate}>New</button>}
          </div>

          {error && <div className="config-banner error">{error}</div>}
          {status && <div className="config-banner success">{status}</div>}

          <form className="agent-form" onSubmit={handleSave}>
            <label>
              <span>File</span>
              <input
                value={form.file}
                disabled={isEditing}
                onChange={event => setForm(prev => ({ ...prev, file: event.target.value }))}
                placeholder="developer.agent.md"
              />
            </label>

            <div className="agent-form-row two-up">
              <label>
                <span>Name</span>
                <input
                  value={form.name}
                  onChange={event => setForm(prev => ({ ...prev, name: event.target.value }))}
                  placeholder="Developer"
                />
              </label>
              <label>
                <span>Backend</span>
                <select
                  value={form.backend}
                  onChange={event => handleBackendChange(event.target.value)}
                >
                  <option value="anthropic">anthropic</option>
                  <option value="copilot">copilot</option>
                </select>
              </label>
            </div>

            <div className="agent-form-row two-up">
              <label>
                <span>Model</span>
                <input
                  value={form.model}
                  onChange={event => setForm(prev => ({ ...prev, model: event.target.value }))}
                  placeholder={form.backend === 'copilot' ? DEFAULT_COPILOT_MODEL : DEFAULT_ANTHROPIC_MODEL}
                />
              </label>
              <label className="agent-checkbox">
                <span>Enabled</span>
                <input
                  type="checkbox"
                  checked={form.isEnabled}
                  onChange={event => setForm(prev => ({ ...prev, isEnabled: event.target.checked }))}
                />
              </label>
            </div>

            <label>
              <span>Brief Description</span>
              <input
                value={form.description}
                onChange={event => setForm(prev => ({ ...prev, description: event.target.value }))}
                placeholder="Handles software engineering and code review tasks."
              />
            </label>

            <label>
              <span>MCP Servers</span>
              <textarea
                value={mcpText}
                onChange={event => setForm(prev => ({ ...prev, mcpServers: normalizeList(event.target.value) }))}
                rows={4}
                placeholder={'filesystem\npostgres'}
              />
            </label>

            <div className="agent-permissions-panel">
              <div className="agent-permissions-header">
                <div>
                  <h3>Permissions</h3>
                  <p>Use values like auto_approve, ask, or deny.</p>
                </div>
                <div className="agent-permissions-actions">
                  <button type="button" className="secondary-btn" onClick={() => applyPermissionTemplate(BACKEND_DEFAULT_TEMPLATE[(form.backend === 'copilot' ? 'copilot' : 'anthropic') as BackendKind])}>Apply Backend Defaults</button>
                  <button type="button" className="secondary-btn" onClick={addPermissionRow}>Add Rule</button>
                </div>
              </div>

              <div className="agent-template-row">
                <button type="button" className="secondary-btn" onClick={() => applyPermissionTemplate('workspace-editor')}>Workspace Editor</button>
                <button type="button" className="secondary-btn" onClick={() => applyPermissionTemplate('read-only-filesystem')}>Read-Only FS</button>
                <button type="button" className="secondary-btn" onClick={() => applyPermissionTemplate('ask-all')}>Ask All</button>
                <button type="button" className="secondary-btn" onClick={() => applyPermissionTemplate('deny-all')}>Deny All</button>
              </div>

              <div className="permission-grid">
                {permissionRows.map((row, index) => (
                  <div key={`${index}-${row.pattern}-${row.value}`} className="permission-row">
                    <input
                      value={row.pattern}
                      onChange={event => updatePermissionRow(index, 'pattern', event.target.value)}
                      placeholder="read_file"
                    />
                    <select
                      value={row.value}
                      onChange={event => updatePermissionRow(index, 'value', event.target.value)}
                    >
                      <option value="auto_approve">auto_approve</option>
                      <option value="ask">ask</option>
                      <option value="deny">deny</option>
                    </select>
                    <button type="button" className="icon-btn" onClick={() => removePermissionRow(index)} aria-label="Remove permission rule">✕</button>
                  </div>
                ))}
              </div>
            </div>

            <label>
              <span>System Prompt</span>
              <textarea
                value={form.systemPrompt}
                onChange={event => setForm(prev => ({ ...prev, systemPrompt: event.target.value }))}
                rows={12}
                placeholder="You are a software development assistant..."
              />
            </label>

            <div className="agent-form-actions">
              <button type="submit" className="send-btn" disabled={saving}>{saving ? 'Saving…' : isEditing ? 'Save Changes' : 'Create Agent'}</button>
              <button type="button" className="secondary-btn" onClick={() => selectedAgent ? beginEdit(selectedAgent) : beginCreate()} disabled={saving}>Reset</button>
            </div>
          </form>
        </section>
      </div>

      {deleteTarget && (
        <div className="modal-overlay" onClick={() => { setDeleteTarget(null); setDeleteConfirmation(''); }}>
          <div className="modal delete-modal" onClick={event => event.stopPropagation()}>
            <h2>Delete Agent</h2>
            <p>
              Type <strong>{deleteTarget.file}</strong> to permanently delete this definition.
            </p>
            {deleteTarget.sessionCount > 0 && (
              <div className="delete-warning-block">
                <p>
                  This agent has <strong>{deleteTarget.sessionCount}</strong> linked session{deleteTarget.sessionCount === 1 ? '' : 's'}.
                  Deleting it without purging those sessions is blocked.
                </p>
                <label className="delete-purge-toggle">
                  <input
                    type="checkbox"
                    checked={purgeLinkedSessions}
                    onChange={event => setPurgeLinkedSessions(event.target.checked)}
                  />
                  <span>Also permanently delete linked sessions and messages</span>
                </label>
              </div>
            )}
            <input
              className="delete-confirm-input"
              value={deleteConfirmation}
              onChange={event => setDeleteConfirmation(event.target.value)}
              placeholder={deleteTarget.file}
            />
            <div className="delete-modal-actions">
              <button className="modal-cancel" onClick={() => { setDeleteTarget(null); setDeleteConfirmation(''); }}>Cancel</button>
              <button
                className="danger-btn"
                onClick={() => void handleDelete()}
                disabled={deleteConfirmation !== deleteTarget.file || saving || (deleteTarget.sessionCount > 0 && !purgeLinkedSessions)}
              >
                Delete Permanently
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}