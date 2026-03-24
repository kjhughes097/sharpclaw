import { useState } from 'react';
import type { ToolResultEvent } from './types';

interface ToolCallBlockProps {
  tool: string;
  input: Record<string, unknown> | null;
  result?: ToolResultEvent;
}

export function ToolCallBlock({ tool, input, result }: ToolCallBlockProps) {
  const [expanded, setExpanded] = useState(false);

  const isRunning = !result;
  const isError = result?.isError ?? false;

  return (
    <div className="tool-block">
      <div className="tool-header" onClick={() => setExpanded(!expanded)}>
        <span className={`tool-chevron ${expanded ? 'open' : ''}`}>▶</span>
        <span>🔧 {tool}</span>
        <span className={`tool-status ${isRunning ? 'running' : isError ? 'error' : 'success'}`}>
          {isRunning ? '⏳ running…' : isError ? '✗ error' : '✓ done'}
        </span>
      </div>
      {expanded && (
        <div className="tool-body">
          <div className="tool-body-section">
            <div className="tool-body-label">Input</div>
            {input ? JSON.stringify(input, null, 2) : '(none)'}
          </div>
          {result && (
            <div className="tool-body-section">
              <div className="tool-body-label">Result</div>
              {result.result}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
