import { useEffect, useMemo, useState } from 'react';
import { createMcp, deleteMcp, fetchAgents, fetchMcps, setMcpEnabled, updateAgent, updateMcp } from './api';
import type { AgentDefinition, AgentUpsertRequest, McpDefinition, McpUpsertRequest } from './types';

interface McpConfigViewProps {
  onMenuClick: () => void;
}

function blankMcp(): McpUpsertRequest {
  return {
    slug: '',
    name: '',
    description: '',
    command: 'npx',
    args: ['-y'],
    isEnabled: true,
    url: null,
  };
}

function toForm(mcp: McpDefinition): McpUpsertRequest {
  return {
    slug: mcp.slug,
    name: mcp.name,
    description: mcp.description,
    command: mcp.command,
    args: [...mcp.args],
    isEnabled: mcp.isEnabled,
    url: mcp.url,
  };
}

function toAgentPayload(agent: AgentDefinition): AgentUpsertRequest {
  return {
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

function formatArgs(args: string[]): string {
  return JSON.stringify(args, null, 2);
}

function parseArgs(value: string): string[] {
  const parsed = JSON.parse(value) as unknown;
  if (!Array.isArray(parsed)) {
    throw new Error('args must be a JSON array of strings.');
  }

  if (parsed.some(item => typeof item !== 'string')) {
    throw new Error('Every args entry must be a string.');
  }

  return parsed.map(item => item.trim()).filter(Boolean);
}

export function McpConfigView({ onMenuClick }: McpConfigViewProps) {
  const [mcps, setMcps] = useState<McpDefinition[]>([]);
  const [agents, setAgents] = useState<AgentDefinition[]>([]);
  const [selectedSlug, setSelectedSlug] = useState<string | null>(null);
  const [mode, setMode] = useState<'list' | 'edit'>('list');
  const [form, setForm] = useState<McpUpsertRequest>(blankMcp);
  const [argsText, setArgsText] = useState(formatArgs(blankMcp().args));
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [linkingAgentId, setLinkingAgentId] = useState<string | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<McpDefinition | null>(null);
  const [deleteConfirmation, setDeleteConfirmation] = useState('');
  const [detachLinkedAgents, setDetachLinkedAgents] = useState(false);

  const selectedMcp = useMemo(
    () => mcps.find(mcp => mcp.slug === selectedSlug) ?? null,
    [mcps, selectedSlug],
  );

  const linkedAgents = useMemo(
    () => selectedMcp ? agents.filter(agent => agent.mcpServers.includes(selectedMcp.slug)) : [],
    [agents, selectedMcp],
  );

  useEffect(() => {
    void loadData();
  }, []);

  useEffect(() => {
    if (!selectedMcp) return;
    setForm(toForm(selectedMcp));
    setArgsText(formatArgs(selectedMcp.args));
  }, [selectedMcp]);

  async function loadData(preferredSelection?: string | null) {
    setLoading(true);
    setError(null);
    try {
      const [mcpResult, agentResult] = await Promise.all([fetchMcps(), fetchAgents()]);
      setMcps(mcpResult);
      setAgents(agentResult);

      const nextSelection = preferredSelection
        ?? (selectedSlug && mcpResult.some(mcp => mcp.slug === selectedSlug) ? selectedSlug : mcpResult[0]?.slug ?? null);

      setSelectedSlug(nextSelection);

      if (!nextSelection) {
        const nextForm = blankMcp();
        setForm(nextForm);
        setArgsText(formatArgs(nextForm.args));
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  function beginCreate() {
    const nextForm = blankMcp();
    setSelectedSlug(null);
    setMode('edit');
    setForm(nextForm);
    setArgsText(formatArgs(nextForm.args));
    setDeleteTarget(null);
    setDeleteConfirmation('');
    setDetachLinkedAgents(false);
    setStatus(null);
    setError(null);
  }

  function beginEdit(mcp: McpDefinition) {
    setSelectedSlug(mcp.slug);
    setMode('edit');
    setForm(toForm(mcp));
    setArgsText(formatArgs(mcp.args));
    setDeleteTarget(null);
    setDeleteConfirmation('');
    setDetachLinkedAgents(false);
    setStatus(null);
    setError(null);
  }

  async function handleSave(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaving(true);
    setError(null);
    setStatus(null);

    try {
      const payload: McpUpsertRequest = {
        ...form,
        slug: form.slug.trim(),
        name: form.name.trim(),
        description: form.description.trim(),
        command: form.url ? '' : form.command.trim(),
        args: form.url ? [] : parseArgs(argsText),
        url: form.url ? form.url.trim() : null,
      };

      const saved = selectedMcp
        ? await updateMcp(selectedMcp.slug, payload)
        : await createMcp(payload);

      setStatus(selectedMcp ? 'MCP updated.' : 'MCP created.');
      await loadData(saved.slug);
      setMode('list');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  async function handleToggle(mcp: McpDefinition) {
    setError(null);
    setStatus(null);
    try {
      await setMcpEnabled(mcp.slug, !mcp.isEnabled);
      setStatus(`${mcp.name} ${mcp.isEnabled ? 'disabled' : 'enabled'}.`);
      await loadData(mcp.slug);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleAgentLinkToggle(agent: AgentDefinition) {
    if (!selectedMcp)
      return;

    setLinkingAgentId(agent.id);
    setError(null);
    setStatus(null);

    try {
      const hasLink = agent.mcpServers.includes(selectedMcp.slug);
      const payload = toAgentPayload(agent);
      payload.mcpServers = hasLink
        ? payload.mcpServers.filter(server => server !== selectedMcp.slug)
        : [...payload.mcpServers, selectedMcp.slug];

      await updateAgent(agent.id, payload);
      setStatus(
        hasLink
          ? `Removed ${selectedMcp.name} from ${agent.name}.`
          : `Linked ${selectedMcp.name} to ${agent.name}.`,
      );
      await loadData(selectedMcp.slug);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLinkingAgentId(null);
    }
  }

  async function handleDelete() {
    if (!deleteTarget) return;

    setSaving(true);
    setError(null);
    setStatus(null);

    try {
      const result = await deleteMcp(deleteTarget.slug, detachLinkedAgents);
      setStatus(result.detachedAgents > 0
        ? `Deleted ${deleteTarget.name} and detached it from ${result.detachedAgents} agent(s).`
        : `Deleted ${deleteTarget.name}.`);
      setDeleteTarget(null);
      setDeleteConfirmation('');
      setDetachLinkedAgents(false);
      await loadData();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  const isEditing = selectedMcp !== null;

  return (
    <div className="chat-area">
      <div className="chat-header">
        <button className="menu-btn" onClick={onMenuClick} aria-label="Open menu">☰</button>
        <div className="config-header-copy">
          <strong>Configure MCPs</strong>
          <span>Manage the stored MCP registry and runtime availability.</span>
        </div>
      </div>

      {mode === 'list' ? (
        <section className="agent-catalog agent-catalog-full">
          <div className="agent-catalog-header">
            <div>
              <h2>MCP Registry</h2>
              <p>{mcps.length} defined</p>
            </div>
            <button className="new-session-btn agent-add-btn" onClick={beginCreate}>+ Add MCP</button>
          </div>

          {error && <div className="config-banner error">{error}</div>}
          {status && <div className="config-banner success">{status}</div>}

          {loading ? (
            <div className="config-empty-state">Loading MCPs…</div>
          ) : mcps.length === 0 ? (
            <div className="config-empty-state">No MCPs are defined yet.</div>
          ) : (
            <div className="agent-card-list">
              {mcps.map(mcp => (
                <article key={mcp.slug} className="agent-card">
                  <div className="agent-card-top">
                    <div>
                      <h3>{mcp.name}</h3>
                      <div className="agent-card-file">{mcp.slug}</div>
                    </div>
                    <span className={`agent-status-pill ${mcp.isEnabled ? 'enabled' : 'disabled'}`}>
                      {mcp.isEnabled ? 'Enabled' : 'Disabled'}
                    </span>
                  </div>

                  <p className="agent-card-description">{mcp.description}</p>

                  <div className="agent-meta-grid">
                    <span className="mcp-card-command">{mcp.url ?? mcp.command}</span>
                    {!mcp.url && <span>{mcp.args.length} arg{mcp.args.length === 1 ? '' : 's'}</span>}
                    <span>{mcp.linkedAgentCount} linked agent{mcp.linkedAgentCount === 1 ? '' : 's'}</span>
                  </div>

                  <div className="agent-chip-row">
                    {mcp.args.length > 0 ? mcp.args.map(arg => (
                      <span key={arg} className="agent-chip">{arg}</span>
                    )) : <span className="agent-chip muted">No args</span>}
                  </div>

                  <div className="agent-card-actions" onClick={event => event.stopPropagation()}>
                    <button className="secondary-btn" onClick={() => beginEdit(mcp)}>Edit</button>
                    <button className="secondary-btn" onClick={() => void handleToggle(mcp)}>
                      {mcp.isEnabled ? 'Disable' : 'Enable'}
                    </button>
                    <button className="danger-btn" onClick={() => { setDeleteTarget(mcp); setDeleteConfirmation(''); setDetachLinkedAgents(false); }}>Delete</button>
                  </div>
                </article>
              ))}
            </div>
          )}
        </section>
      ) : (
        <section className="agent-editor-panel agent-editor-standalone">
          <div className="agent-editor-header">
            <div>
              <h2>{isEditing ? 'Edit MCP' : 'New MCP'}</h2>
              <p>{isEditing ? 'Update the stored runtime definition for this MCP.' : 'Create a new MCP definition in the database.'}</p>
            </div>
            <div className="agent-editor-actions">
              {isEditing && <button className="secondary-btn" onClick={beginCreate}>New</button>}
              <button className="secondary-btn" onClick={() => setMode('list')}>Back to MCPs</button>
            </div>
          </div>

          {error && <div className="config-banner error">{error}</div>}
          {status && <div className="config-banner success">{status}</div>}

          <form className="agent-form" onSubmit={handleSave}>
            <label>
              <span>Slug</span>
              <input
                value={form.slug}
                disabled={isEditing}
                onChange={event => setForm(prev => ({ ...prev, slug: event.target.value }))}
                placeholder="github"
              />
            </label>

            <div className="agent-form-row two-up">
              <label>
                <span>Name</span>
                <input
                  value={form.name}
                  onChange={event => setForm(prev => ({ ...prev, name: event.target.value }))}
                  placeholder="GitHub"
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
              <span>Description</span>
              <input
                value={form.description}
                onChange={event => setForm(prev => ({ ...prev, description: event.target.value }))}
                placeholder="Interact with GitHub repositories, issues, and pull requests."
              />
            </label>

            <div className="agent-form-row two-up">
              <label>
                <span>Transport</span>
                <select
                  value={form.url !== null ? 'remote' : 'stdio'}
                  onChange={event => {
                    if (event.target.value === 'remote') {
                      setForm(prev => ({ ...prev, url: '', command: '', args: [] }));
                      setArgsText('[]');
                    } else {
                      setForm(prev => ({ ...prev, url: null, command: 'npx', args: ['-y'] }));
                      setArgsText(formatArgs(['-y']));
                    }
                  }}
                >
                  <option value="stdio">Stdio (local command)</option>
                  <option value="remote">Remote (HTTP/SSE URL)</option>
                </select>
              </label>
            </div>

            {form.url !== null ? (
              <label>
                <span>URL</span>
                <input
                  value={form.url}
                  onChange={event => setForm(prev => ({ ...prev, url: event.target.value }))}
                  placeholder="https://mcp.example.com/sse"
                />
              </label>
            ) : (
              <>
                <label>
                  <span>Command</span>
                  <input
                    value={form.command}
                    onChange={event => setForm(prev => ({ ...prev, command: event.target.value }))}
                    placeholder="npx"
                  />
                </label>

                <label>
                  <span>Args (JSON Array)</span>
                  <textarea
                    className="json-textarea"
                    value={argsText}
                    onChange={event => setArgsText(event.target.value)}
                    rows={8}
                    placeholder={'[\n  "-y",\n  "@modelcontextprotocol/server-github"\n]'}
                  />
                </label>
              </>
            )}

            <div className="mcp-selection-panel">
              <div className="agent-permissions-header">
                <div>
                  <h3>Linked Agents</h3>
                  <p>Manage which agents can access this MCP directly from the MCP editor.</p>
                </div>
              </div>

              {!isEditing ? (
                <div className="config-empty-state compact">Save this MCP first, then link agents to it.</div>
              ) : agents.length === 0 ? (
                <div className="config-empty-state compact">No agents are defined yet.</div>
              ) : (
                <>
                  <div className="link-summary-panel">
                    <div className="link-summary-copy">
                      <strong>Currently Linked</strong>
                      <span>{linkedAgents.length === 0 ? 'No agents linked yet.' : 'Click any linked agent below to remove it.'}</span>
                    </div>
                    <div className="linked-chip-row">
                      {linkedAgents.length > 0 ? linkedAgents.map(agent => (
                        <button
                          key={agent.id}
                          type="button"
                          className="link-chip"
                          onClick={() => void handleAgentLinkToggle(agent)}
                          disabled={linkingAgentId === agent.id}
                          title={`Remove ${selectedMcp?.name} from ${agent.name}`}
                        >
                          <span>{agent.name}</span>
                          <span className="link-chip-action">Remove</span>
                        </button>
                      )) : (
                        <span className="agent-chip muted">Use the cards below to link agents.</span>
                      )}
                    </div>
                  </div>

                  <div className="mcp-selection-grid">
                    {agents.map(agent => {
                      const linked = !!selectedMcp && agent.mcpServers.includes(selectedMcp.slug);
                      return (
                        <button
                          key={agent.id}
                          type="button"
                          className={`mcp-option agent-link-option ${linked ? 'selected' : ''} ${agent.isEnabled ? '' : 'disabled'}`}
                          onClick={() => void handleAgentLinkToggle(agent)}
                          disabled={linkingAgentId === agent.id}
                          aria-pressed={linked}
                        >
                          <div className="mcp-option-top">
                            <strong>{agent.name}</strong>
                            <div className="mcp-option-badges">
                              <span className={`permission-state-badge ${linked ? 'recommended' : 'no-rules'}`}>
                                {linked ? 'Linked' : 'Available'}
                              </span>
                              <span className={`agent-status-pill ${agent.isEnabled ? 'enabled' : 'disabled'}`}>
                                {agent.isEnabled ? 'Enabled' : 'Disabled'}
                              </span>
                            </div>
                          </div>
                          <div className="mcp-option-slug">{agent.id}</div>
                          <p className="mcp-option-description">{agent.description}</p>
                          <div className="mcp-option-meta">
                            <span>{agent.backend}</span>
                            <span>{agent.mcpServers.length} MCP{agent.mcpServers.length === 1 ? '' : 's'}</span>
                          </div>
                        </button>
                      );
                    })}
                  </div>
                </>
              )}
            </div>

            <div className="agent-form-actions">
              <button type="submit" className="send-btn" disabled={saving}>{saving ? 'Saving…' : isEditing ? 'Save Changes' : 'Create MCP'}</button>
              <button type="button" className="secondary-btn" onClick={() => selectedMcp ? beginEdit(selectedMcp) : beginCreate()} disabled={saving}>Reset</button>
              <button type="button" className="secondary-btn" onClick={() => setMode('list')} disabled={saving}>Cancel</button>
            </div>
          </form>
        </section>
      )}

      {deleteTarget && (
        <div className="modal-overlay" onClick={() => { setDeleteTarget(null); setDeleteConfirmation(''); }}>
          <div className="modal delete-modal" onClick={event => event.stopPropagation()}>
            <h2>Delete MCP</h2>
            <p>
              Type <strong>{deleteTarget.slug}</strong> to permanently delete this MCP definition.
            </p>
            {deleteTarget.linkedAgentCount > 0 && (
              <div className="delete-warning-block">
                <p>
                  This MCP is linked to <strong>{deleteTarget.linkedAgentCount}</strong> agent{deleteTarget.linkedAgentCount === 1 ? '' : 's'}.
                  Deleting it without detaching those agents is blocked.
                </p>
                <label className="delete-purge-toggle">
                  <input
                    type="checkbox"
                    checked={detachLinkedAgents}
                    onChange={event => setDetachLinkedAgents(event.target.checked)}
                  />
                  <span>Also remove this MCP from linked agents</span>
                </label>
              </div>
            )}
            <input
              className="delete-confirm-input"
              value={deleteConfirmation}
              onChange={event => setDeleteConfirmation(event.target.value)}
              placeholder={deleteTarget.slug}
            />
            <div className="delete-modal-actions">
              <button className="modal-cancel" onClick={() => { setDeleteTarget(null); setDeleteConfirmation(''); }}>Cancel</button>
              <button
                className="danger-btn"
                onClick={() => void handleDelete()}
                disabled={deleteConfirmation !== deleteTarget.slug || saving || (deleteTarget.linkedAgentCount > 0 && !detachLinkedAgents)}
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