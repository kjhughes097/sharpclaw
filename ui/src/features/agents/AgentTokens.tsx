import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useId, useState, type FormEvent } from 'react';
import { api, call } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import type { TokenMetaDto } from '../../api/types';
import { useToast } from '../../app/toast';
import {
  Badge,
  Button,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  LoadingState,
  RelativeTime,
} from '../../components/ui';
import { formatAbsolute } from '../../lib/time';
import { describedBy, Field } from './AgentParts';
import { inputClass, SECRET_MUTATION, useAgentTokens } from './agentQueries';
import { TokenRevealDialog } from './TokenRevealDialog';

function tokenStatus(token: TokenMetaDto) {
  if (token.revokedAt) return <Badge tone="danger">revoked</Badge>;
  if (token.expiresAt && Date.parse(token.expiresAt) <= Date.now()) return <Badge tone="warn">expired</Badge>;
  return <Badge tone="ok">active</Badge>;
}

export function TokensSection({ name }: { name: string }) {
  const id = useId();
  const tokens = useAgentTokens(name);
  const queryClient = useQueryClient();
  const toast = useToast();
  const [expiry, setExpiry] = useState('');
  const [issueError, setIssueError] = useState<string | null>(null);
  // The issued token's value: held only while the reveal dialog is open, then cleared.
  const [revealed, setRevealed] = useState<string | null>(null);
  const [revoking, setRevoking] = useState<TokenMetaDto | null>(null);

  const issue = useMutation({
    ...SECRET_MUTATION,
    mutationFn: (expiresAt: string | null) =>
      call(() =>
        api.POST('/api/agents/{idOrName}/tokens', {
          params: { path: { idOrName: name } },
          body: { expiresAt },
        }),
      ),
  });

  const revoke = useMutation({
    mutationFn: (tokenId: string) =>
      call(() =>
        api.DELETE('/api/agents/{idOrName}/tokens/{tokenId}', {
          params: { path: { idOrName: name, tokenId } },
        }),
      ),
    onSettled: () => queryClient.invalidateQueries({ queryKey: keys.tokens(name) }),
  });

  function submitIssue(e: FormEvent) {
    e.preventDefault();
    setIssueError(null);
    let expiresAt: string | null = null;
    if (expiry) {
      // The end of the chosen day, local time.
      const end = new Date(`${expiry}T23:59:59`);
      if (Number.isNaN(end.getTime()) || end.getTime() <= Date.now()) {
        setIssueError('Choose a date in the future, or leave it empty.');
        return;
      }
      expiresAt = end.toISOString();
    }
    issue.mutate(expiresAt, {
      onSuccess: (result) => {
        void queryClient.invalidateQueries({ queryKey: keys.tokens(name) });
        setExpiry('');
        setRevealed(result.token.value);
        issue.reset();
      },
      onError: (err) => setIssueError(errorMessage(err)),
    });
  }

  const expiryId = `${id}-expiry`;

  return (
    <section
      aria-labelledby={`${id}-title`}
      className="space-y-3 rounded-lg border border-border bg-surface p-4"
    >
      <h2 id={`${id}-title`} className="font-semibold">
        Tokens
      </h2>
      <form onSubmit={submitIssue} noValidate className="flex flex-wrap items-end gap-3">
        <Field id={expiryId} label="Expires on (optional)" error={issueError}>
          <input
            id={expiryId}
            name="expiresAt"
            type="date"
            value={expiry}
            onChange={(e) => setExpiry(e.target.value)}
            className={inputClass}
            {...describedBy(expiryId, false, issueError)}
          />
        </Field>
        <Button type="submit" variant="primary" busy={issue.isPending}>
          Issue token
        </Button>
      </form>
      {tokens.isPending ? (
        <LoadingState label="Loading tokens" />
      ) : tokens.isError ? (
        <ErrorState message={errorMessage(tokens.error)} onRetry={() => void tokens.refetch()} />
      ) : tokens.data.length === 0 ? (
        <EmptyState title="No tokens." />
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[40rem] text-left text-sm">
            <caption className="sr-only">Tokens for {name}</caption>
            <thead className="text-muted">
              <tr>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Prefix
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Created
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Expires
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Last used
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Revoked
                </th>
                <th scope="col" className="py-2 font-medium">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {tokens.data.map((token) => (
                <tr key={token.id} className="border-t border-border">
                  <td className="py-2 pr-3 font-mono">{token.prefix}</td>
                  <td className="py-2 pr-3">
                    <RelativeTime iso={token.createdAt} />
                  </td>
                  <td className="py-2 pr-3">{token.expiresAt ? formatAbsolute(token.expiresAt) : 'never'}</td>
                  <td className="py-2 pr-3">
                    {token.lastUsedAt ? <RelativeTime iso={token.lastUsedAt} /> : 'never'}
                  </td>
                  <td className="py-2 pr-3">
                    <span className="flex flex-wrap items-center gap-2">
                      {tokenStatus(token)}
                      {token.revokedAt && <RelativeTime iso={token.revokedAt} />}
                    </span>
                  </td>
                  <td className="py-2 text-right">
                    {!token.revokedAt && (
                      <Button
                        variant="ghost"
                        aria-label={`Revoke token ${token.prefix}`}
                        onClick={() => setRevoking(token)}
                      >
                        Revoke
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <ConfirmDialog
        open={revoking !== null}
        onOpenChange={(open) => {
          if (!open) setRevoking(null);
        }}
        title="Revoke token?"
        description={`The agent using the token starting ${revoking?.prefix ?? ''} will lose access immediately.`}
        confirmLabel="Revoke"
        danger
        busy={revoke.isPending}
        onConfirm={() => {
          if (!revoking) return;
          revoke.mutate(revoking.id, {
            onSuccess: () => toast('Token revoked.', 'success'),
            onError: (err) => toast(errorMessage(err), 'error'),
            onSettled: () => setRevoking(null),
          });
        }}
      />
      <TokenRevealDialog value={revealed} onClose={() => setRevealed(null)} />
    </section>
  );
}
