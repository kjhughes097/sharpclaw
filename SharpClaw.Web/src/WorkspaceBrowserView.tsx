import { useCallback, useEffect, useState } from 'react';
import { fetchWorkspaceContents } from './api';
import type { WorkspaceEntry } from './types';

interface WorkspaceBrowserViewProps {
  onMenuClick: () => void;
}

function formatSize(bytes: number | null): string {
  if (bytes === null) return '—';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

function formatDate(dateStr: string | null): string {
  if (!dateStr) return '—';
  const date = new Date(dateStr);
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

export function WorkspaceBrowserView({ onMenuClick }: WorkspaceBrowserViewProps) {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [currentPath, setCurrentPath] = useState('');
  const [entries, setEntries] = useState<WorkspaceEntry[]>([]);

  const load = useCallback(async (path?: string) => {
    setLoading(true);
    setError(null);

    try {
      const result = await fetchWorkspaceContents(path);
      setCurrentPath(result.path);
      setEntries(result.entries);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const navigateToDir = useCallback((name: string) => {
    const newPath = currentPath ? `${currentPath}/${name}` : name;
    void load(newPath);
  }, [currentPath, load]);

  const navigateUp = useCallback(() => {
    if (!currentPath) return;
    const parts = currentPath.split('/');
    parts.pop();
    const parentPath = parts.join('/');
    void load(parentPath || undefined);
  }, [currentPath, load]);

  const navigateToBreadcrumb = useCallback((index: number) => {
    if (index < 0) {
      void load();
      return;
    }
    const parts = currentPath.split('/');
    const newPath = parts.slice(0, index + 1).join('/');
    void load(newPath);
  }, [currentPath, load]);

  const pathParts = currentPath ? currentPath.split('/') : [];

  return (
    <div className="chat-area">
      <div className="chat-header">
        <button className="menu-btn" onClick={onMenuClick} aria-label="Open menu">☰</button>
        <div className="config-header-copy">
          <strong>Workspace Browser</strong>
          <span>Browse files and directories in the workspace.</span>
        </div>
      </div>

      {error && <div className="config-banner error">{error}</div>}

      <div className="workspace-browser-content">
        {/* Breadcrumb navigation */}
        <nav className="workspace-breadcrumb" aria-label="Workspace breadcrumb">
          <button
            className={`workspace-breadcrumb-item ${pathParts.length === 0 ? 'active' : ''}`}
            onClick={() => navigateToBreadcrumb(-1)}
            disabled={loading}
          >
            workspace
          </button>
          {pathParts.map((part, idx) => (
            <span key={idx}>
              <span className="workspace-breadcrumb-sep">/</span>
              <button
                className={`workspace-breadcrumb-item ${idx === pathParts.length - 1 ? 'active' : ''}`}
                onClick={() => navigateToBreadcrumb(idx)}
                disabled={loading}
              >
                {part}
              </button>
            </span>
          ))}
        </nav>

        {loading ? (
          <div className="config-empty-state">Loading workspace contents…</div>
        ) : entries.length === 0 ? (
          <div className="config-empty-state">
            This directory is empty.
            {currentPath && (
              <button className="workspace-back-link" onClick={navigateUp}>
                ← Go back
              </button>
            )}
          </div>
        ) : (
          <div className="workspace-file-list">
            {/* Up navigation row */}
            {currentPath && (
              <button className="workspace-entry workspace-entry-up" onClick={navigateUp}>
                <span className="workspace-entry-icon">📁</span>
                <span className="workspace-entry-name">..</span>
                <span className="workspace-entry-size" />
                <span className="workspace-entry-date" />
              </button>
            )}

            {/* Directory entries first, then files */}
            {entries.map(entry => (
              <div
                key={entry.name}
                className={`workspace-entry ${entry.type === 'directory' ? 'workspace-entry-dir' : 'workspace-entry-file'}`}
                onClick={entry.type === 'directory' ? () => navigateToDir(entry.name) : undefined}
                role={entry.type === 'directory' ? 'button' : undefined}
                tabIndex={entry.type === 'directory' ? 0 : undefined}
                onKeyDown={entry.type === 'directory' ? (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); navigateToDir(entry.name); } } : undefined}
              >
                <span className="workspace-entry-icon" aria-hidden="true">
                  {entry.type === 'directory' ? '📁' : '📄'}
                </span>
                <span className="workspace-entry-name">{entry.name}</span>
                <span className="workspace-entry-size">{entry.type === 'file' ? formatSize(entry.size) : ''}</span>
                <span className="workspace-entry-date">{formatDate(entry.lastModified)}</span>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
