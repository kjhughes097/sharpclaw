import { useState } from 'react';
import type { PermissionRequestEvent } from './types';
import { resolvePermission } from './api';

interface PermissionCardProps {
  sessionId: string;
  event: PermissionRequestEvent;
}

export function PermissionCard({ sessionId, event }: PermissionCardProps) {
  const [resolved, setResolved] = useState<'allowed' | 'denied' | null>(null);
  const [loading, setLoading] = useState(false);

  const handleDecision = async (allow: boolean) => {
    setLoading(true);
    try {
      await resolvePermission(sessionId, event.requestId, allow);
      setResolved(allow ? 'allowed' : 'denied');
    } catch (err) {
      console.error('Permission resolve error:', err);
      setLoading(false);
    }
  };

  return (
    <div className="permission-card">
      <div className="perm-title">⚠ Permission Required</div>
      <div className="perm-tool">Tool: {event.tool}</div>
      {event.input && (
        <div className="perm-input">
          {JSON.stringify(event.input, null, 2)}
        </div>
      )}

      {resolved ? (
        <div className={`perm-resolved ${resolved}`}>
          {resolved === 'allowed' ? '✓ Allowed' : '✗ Denied'}
        </div>
      ) : (
        <div className="perm-actions">
          <button
            className="perm-btn allow"
            onClick={() => handleDecision(true)}
            disabled={loading}
          >
            Allow
          </button>
          <button
            className="perm-btn deny"
            onClick={() => handleDecision(false)}
            disabled={loading}
          >
            Deny
          </button>
        </div>
      )}
    </div>
  );
}
