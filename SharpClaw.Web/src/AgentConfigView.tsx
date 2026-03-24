import { useEffect, useMemo, useState } from 'react';
import { createAgent, deleteAgent, fetchAgents, fetchMcps, setAgentEnabled, updateAgent } from './api';
import type { AgentDefinition, AgentUpsertRequest, McpDefinition } from './types';

interface AgentConfigViewProps {
  onMenuClick: () => void;
}

interface PermissionRow {
  pattern: string;
  value: string;
}

interface PermissionGroup {
  key: string;
  title: string;
  description: string;
  indices: number[];
  defaultPattern: string;
}

type McpPermissionStatus = 'recommended' | 'customized' | 'no-rules';

type BackendKind = 'anthropic' | 'copilot';

const DEFAULT_ANTHROPIC_MODEL = 'claude-haiku-4-5-20251001';
const DEFAULT_COPILOT_MODEL = 'gpt-5.4';

const PERMISSION_TEMPLATES: Record<string, Record<string, string>> = {
  'workspace-editor': {
    'filesystem.read_*': 'auto_approve',
    'filesystem.list_*': 'auto_approve',
    'filesystem.search_*': 'auto_approve',
    'filesystem.get_*': 'auto_approve',
    'filesystem.write_*': 'ask',
    'filesystem.create_*': 'ask',
    'github.*': 'ask',
    '*': 'ask',
  },
  'read-only-filesystem': {
    'filesystem.read_*': 'auto_approve',
    'filesystem.list_*': 'auto_approve',
    'filesystem.search_*': 'auto_approve',
    'filesystem.get_*': 'auto_approve',
    '*': 'ask',
  },
  'ask-all': {
    '*': 'ask',
  },
  'deny-all': {
    '*': 'deny',
  },
};

const MCP_RECOMMENDED_PERMISSIONS: Record<string, Record<string, string>> = {
  filesystem: {
    'filesystem.read_*': 'auto_approve',
    'filesystem.list_*': 'auto_approve',
    'filesystem.search_*': 'auto_approve',
    'filesystem.get_*': 'auto_approve',
    'filesystem.write_*': 'ask',
    'filesystem.create_*': 'ask',
  },
  github: {
    'github.*': 'ask',
  },
  sqlite: {
    'sqlite.*': 'ask',
  },
};

const PERMISSION_NORMALIZATION_FAMILIES: Array<{ wildcard: string; patterns: string[] }> = [
  { wildcard: 'filesystem.read_*', patterns: ['filesystem.read_file'] },
  { wildcard: 'filesystem.list_*', patterns: ['filesystem.list_directory', 'filesystem.list_allowed_directories'] },
  { wildcard: 'filesystem.search_*', patterns: ['filesystem.search_files'] },
  { wildcard: 'filesystem.get_*', patterns: ['filesystem.get_file_info'] },
  { wildcard: 'filesystem.write_*', patterns: ['filesystem.write_file'] },
  { wildcard: 'filesystem.create_*', patterns: ['filesystem.create_directory'] },
];

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

function normalizeStringList(values: string[]): string[] {
  const seen = new Set<string>();
  const normalized: string[] = [];

  for (const rawValue of values) {
    const value = rawValue.trim();
    if (!value) continue;

    const key = value.toLowerCase();
    if (seen.has(key)) continue;

    seen.add(key);
    normalized.push(value);
  }

  return normalized;
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

function normalizePermissions(rows: PermissionRow[]): Record<string, string> {
  return rows.reduce<Record<string, string>>((acc, row) => {
    const pattern = row.pattern.trim();
    const value = row.value.trim().toLowerCase();
    if (pattern && value) acc[pattern] = value;
    return acc;
  }, {});
}

function toSortedPermissionRows(policy: Record<string, string>): PermissionRow[] {
  return Object.entries(policy)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([pattern, value]) => ({ pattern, value }));
}

function applyMcpPresetToPolicy(policy: Record<string, string>, mcpSlug: string): Record<string, string> {
  const preset = MCP_RECOMMENDED_PERMISSIONS[mcpSlug];
  if (!preset) return policy;

  const updated = Object.fromEntries(
    Object.entries(policy).filter(([pattern]) => !pattern.startsWith(`${mcpSlug}.`)),
  );

  return {
    ...updated,
    ...preset,
  };
}

function normalizePermissionFamilies(policy: Record<string, string>): Record<string, string> {
  const normalized = { ...policy };

  for (const family of PERMISSION_NORMALIZATION_FAMILIES) {
    if (family.patterns.some(pattern => !(pattern in normalized)))
      continue;

    const values = family.patterns.map(pattern => normalized[pattern]);
    if (values.some(value => value !== values[0]))
      continue;

    for (const pattern of family.patterns)
      delete normalized[pattern];

    normalized[family.wildcard] = values[0];
  }

  return normalized;
}

function getMcpScopedPolicy(policy: Record<string, string>, mcpSlug: string): Record<string, string> {
  return Object.fromEntries(
    Object.entries(policy).filter(([pattern]) => pattern.startsWith(`${mcpSlug}.`)),
  );
}

function getMcpPermissionStatus(policy: Record<string, string>, mcpSlug: string): McpPermissionStatus {
  const scopedPolicy = getMcpScopedPolicy(policy, mcpSlug);
  if (Object.keys(scopedPolicy).length === 0)
    return 'no-rules';

  const recommended = MCP_RECOMMENDED_PERMISSIONS[mcpSlug];
  if (recommended && policiesEqual(scopedPolicy, recommended))
    return 'recommended';

  return 'customized';
}

function buildPermissionGroups(rows: PermissionRow[], mcps: string[]): PermissionGroup[] {
  const groups: PermissionGroup[] = [
    {
      key: 'global',
      title: 'Global Rules',
      description: 'Catch-all or shared rules such as *.',
      indices: [],
      defaultPattern: '*',
    },
    ...mcps.map(slug => ({
      key: slug,
      title: slug,
      description: `Rules scoped to the ${slug} MCP.`,
      indices: [],
      defaultPattern: `${slug}.*`,
    })),
    {
      key: 'custom',
      title: 'Custom Rules',
      description: 'Rules that do not map cleanly to a selected MCP.',
      indices: [],
      defaultPattern: '',
    },
  ];

  const groupLookup = new Map(groups.map(group => [group.key, group] as const));

  rows.forEach((row, index) => {
    const pattern = row.pattern.trim();
    if (!pattern || pattern === '*') {
      groupLookup.get('global')?.indices.push(index);
      return;
    }

    const separatorIndex = pattern.indexOf('.');
    if (separatorIndex > 0) {
      const slug = pattern.slice(0, separatorIndex);
      const group = groupLookup.get(slug);
      if (group) {
        group.indices.push(index);
        return;
      }
    }

    groupLookup.get('custom')?.indices.push(index);
  });

  return groups.filter(group => group.indices.length > 0 || group.key !== 'custom' || rows.length === 0);
}

export function AgentConfigView({ onMenuClick }: AgentConfigViewProps) {
  const [agents, setAgents] = useState<AgentDefinition[]>([]);
  const [mcps, setMcps] = useState<McpDefinition[]>([]);
  const [selectedFile, setSelectedFile] = useState<string | null>(null);
  const [mode, setMode] = useState<'list' | 'edit'>('list');
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

  const permissionGroups = useMemo(
    () => buildPermissionGroups(permissionRows, form.mcpServers),
    [permissionRows, form.mcpServers],
  );

  const currentPermissionPolicy = useMemo(
    () => normalizePermissions(permissionRows),
    [permissionRows],
  );

  const mcpLookup = useMemo(
    () => new Map(mcps.map(mcp => [mcp.slug, mcp] as const)),
    [mcps],
  );

  useEffect(() => {
    void loadData();
  }, []);

  useEffect(() => {
    if (!selectedAgent) return;
    setForm(toForm(selectedAgent));
    setPermissionRows(toPermissionRows(selectedAgent.permissionPolicy));
  }, [selectedAgent]);

  async function loadData(preferredSelection?: string | null) {
    setLoading(true);
    setError(null);
    try {
      const [agentResult, mcpResult] = await Promise.all([fetchAgents(), fetchMcps()]);
      setAgents(agentResult);
      setMcps(mcpResult);

      const nextSelection = preferredSelection
        ?? (selectedFile && agentResult.some(agent => agent.file === selectedFile) ? selectedFile : agentResult[0]?.file ?? null);

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
    setMode('edit');
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
    setMode('edit');
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

  function applyRecommendedMcpPreset(mcpSlug: string) {
    setPermissionRows(prev => {
      const nextPolicy = applyMcpPresetToPolicy(normalizePermissions(prev), mcpSlug);
      return toSortedPermissionRows(nextPolicy);
    });
    setStatus(`Applied recommended ${mcpSlug} permissions.`);
    setError(null);
  }

  function normalizePermissionFamiliesInForm() {
    setPermissionRows(prev => {
      const nextPolicy = normalizePermissionFamilies(normalizePermissions(prev));
      return toSortedPermissionRows(nextPolicy);
    });
    setStatus('Normalized exact permission rules into wildcard families where safe.');
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

  function addPermissionRowForGroup(defaultPattern: string) {
    setPermissionRows(prev => [...prev, { pattern: defaultPattern, value: 'ask' }]);
  }

  function removePermissionRow(index: number) {
    setPermissionRows(prev => prev.length === 1 ? prev : prev.filter((_, rowIndex) => rowIndex !== index));
  }

  function toggleMcpSelection(slug: string) {
    setForm(prev => ({
      ...prev,
      mcpServers: prev.mcpServers.includes(slug)
        ? prev.mcpServers.filter(server => server !== slug)
        : [...prev.mcpServers, slug],
    }));
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
      mcpServers: normalizeStringList(form.mcpServers),
      permissionPolicy: normalizePermissions(permissionRows),
      systemPrompt: form.systemPrompt.trim(),
    };

    try {
      const saved = selectedAgent
        ? await updateAgent(selectedAgent.file, payload)
        : await createAgent(payload);

      setStatus(selectedAgent ? 'Agent updated.' : 'Agent created.');
      await loadData(saved.file);
      setMode('list');
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
      await loadData(agent.file);
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
      await loadData();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  const isEditing = selectedAgent !== null;

  return (
    <div className="chat-area">
      <div className="chat-header">
        <button className="menu-btn" onClick={onMenuClick} aria-label="Open menu">☰</button>
        <div className="config-header-copy">
          <strong>Configure Agents</strong>
          <span>Manage stored agent definitions and the MCPs they can access.</span>
        </div>
      </div>

      {mode === 'list' ? (
        <section className="agent-catalog agent-catalog-full">
          <div className="agent-catalog-header">
            <div>
              <h2>Agents</h2>
              <p>{agents.length} defined</p>
            </div>
            <button className="new-session-btn agent-add-btn" onClick={beginCreate}>+ Add Agent</button>
          </div>

          {error && <div className="config-banner error">{error}</div>}
          {status && <div className="config-banner success">{status}</div>}

          {loading ? (
            <div className="config-empty-state">Loading agents…</div>
          ) : agents.length === 0 ? (
            <div className="config-empty-state">No agents are defined yet.</div>
          ) : (
            <div className="agent-card-list">
              {agents.map(agent => (
                <article key={agent.file} className="agent-card">
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
                    {agent.mcpServers.length > 0 ? agent.mcpServers.map(server => {
                      const mcp = mcpLookup.get(server);
                      return (
                        <span key={server} className={`agent-chip ${mcp && !mcp.isEnabled ? 'disabled' : ''}`} title={server}>
                          {mcp?.name ?? server}
                        </span>
                      );
                    }) : <span className="agent-chip muted">No MCP servers</span>}
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
      ) : (
        <section className="agent-editor-panel agent-editor-standalone">
          <div className="agent-editor-header">
            <div>
              <h2>{isEditing ? 'Edit Agent' : 'New Agent'}</h2>
              <p>{isEditing ? 'Update the stored definition for this agent.' : 'Create a new agent definition in the database.'}</p>
            </div>
            <div className="agent-editor-actions">
              {isEditing && <button className="secondary-btn" onClick={beginCreate}>New</button>}
              <button className="secondary-btn" onClick={() => setMode('list')}>Back to Agents</button>
            </div>
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

            <div className="mcp-selection-panel">
              <div className="agent-permissions-header">
                <div>
                  <h3>MCP Access</h3>
                  <p>Select the stored MCP servers this agent can reach at runtime.</p>
                </div>
              </div>

              {mcps.length === 0 ? (
                <div className="config-empty-state compact">No MCPs are defined yet.</div>
              ) : (
                <div className="mcp-selection-grid">
                  {mcps.map(mcp => {
                    const selected = form.mcpServers.includes(mcp.slug);
                    const permissionStatus = getMcpPermissionStatus(currentPermissionPolicy, mcp.slug);
                    return (
                      <button
                        key={mcp.slug}
                        type="button"
                        className={`mcp-option ${selected ? 'selected' : ''} ${mcp.isEnabled ? '' : 'disabled'}`}
                        onClick={() => toggleMcpSelection(mcp.slug)}
                        aria-pressed={selected}
                      >
                        <div className="mcp-option-top">
                          <strong>{mcp.name}</strong>
                          <div className="mcp-option-badges">
                            <span className={`permission-state-badge ${permissionStatus}`}>
                              {permissionStatus === 'recommended'
                                ? 'Recommended'
                                : permissionStatus === 'customized'
                                  ? 'Customized'
                                  : 'No Rules'}
                            </span>
                            <span className={`agent-status-pill ${mcp.isEnabled ? 'enabled' : 'disabled'}`}>
                              {mcp.isEnabled ? 'Enabled' : 'Disabled'}
                            </span>
                          </div>
                        </div>
                        <div className="mcp-option-slug">{mcp.slug}</div>
                        <p className="mcp-option-description">{mcp.description}</p>
                        <div className="mcp-option-meta">
                          <span>{mcp.command}</span>
                          <span>{mcp.args.length} arg{mcp.args.length === 1 ? '' : 's'}</span>
                        </div>
                      </button>
                    );
                  })}
                </div>
              )}
            </div>

            <div className="agent-permissions-panel">
              <div className="agent-permissions-header">
                <div>
                  <h3>Permissions</h3>
                  <p>Rules are grouped by MCP. Use MCP-scoped patterns like filesystem.read_* or github.* with values like auto_approve, ask, or deny.</p>
                </div>
                <div className="agent-permissions-actions">
                  <button type="button" className="secondary-btn" onClick={() => applyPermissionTemplate(BACKEND_DEFAULT_TEMPLATE[(form.backend === 'copilot' ? 'copilot' : 'anthropic') as BackendKind])}>Apply Backend Defaults</button>
                  <button type="button" className="secondary-btn" onClick={normalizePermissionFamiliesInForm}>Normalize Families</button>
                  <button type="button" className="secondary-btn" onClick={() => addPermissionRowForGroup('*')}>Add Global Rule</button>
                </div>
              </div>

              <div className="agent-template-row">
                <button type="button" className="secondary-btn" onClick={() => applyPermissionTemplate('workspace-editor')}>Workspace Editor</button>
                <button type="button" className="secondary-btn" onClick={() => applyPermissionTemplate('read-only-filesystem')}>Read-Only FS</button>
                <button type="button" className="secondary-btn" onClick={() => applyPermissionTemplate('ask-all')}>Ask All</button>
                <button type="button" className="secondary-btn" onClick={() => applyPermissionTemplate('deny-all')}>Deny All</button>
              </div>

              <div className="permission-group-list">
                {permissionGroups.map(group => (
                  <section key={group.key} className="permission-group-card">
                    <div className="permission-group-header">
                      <div>
                        <h4>{group.title}</h4>
                        <p>{group.description}</p>
                      </div>
                      <div className="permission-group-actions">
                        {group.key !== 'global' && group.key !== 'custom' && MCP_RECOMMENDED_PERMISSIONS[group.key] && (
                          <button type="button" className="secondary-btn" onClick={() => applyRecommendedMcpPreset(group.key)}>
                            Use Recommended
                          </button>
                        )}
                        <button type="button" className="secondary-btn" onClick={() => addPermissionRowForGroup(group.defaultPattern)}>
                          Add Rule
                        </button>
                      </div>
                    </div>

                    <div className="permission-grid">
                      {group.indices.map(index => {
                        const row = permissionRows[index];
                        return (
                          <div key={`${index}-${row.pattern}-${row.value}`} className="permission-row">
                            <input
                              value={row.pattern}
                              onChange={event => updatePermissionRow(index, 'pattern', event.target.value)}
                              placeholder={group.defaultPattern || 'custom.pattern'}
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
                        );
                      })}
                    </div>
                  </section>
                ))}
              </div>
            </div>

            <label>
              <span>Soul</span>
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
              <button type="button" className="secondary-btn" onClick={() => setMode('list')} disabled={saving}>Cancel</button>
            </div>
          </form>
        </section>
      )}

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