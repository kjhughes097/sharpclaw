import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useId, useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router';
import { api, call } from '../../api/client';
import { ApiError, budgetResetLocalTime, errorMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import type { AgentDto, TokenMetaDto } from '../../api/types';
import { useToast } from '../../app/toast';
import {
  Badge,
  Button,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  LoadingState,
  Meter,
  RelativeTime,
} from '../../components/ui';
import { formatAbsolute, formatCost, formatDuration, formatUsd } from '../../lib/time';
import { ConfigNote, describedBy, EnabledSwitch, Field, LastSeen } from './AgentParts';
import {
  budgetError,
  CONTEXT_MAX,
  CONTEXT_MIN,
  contextWindowError,
  inputClass,
  MODEL_HINT,
  SECRET_MUTATION,
  useAgent,
  useAgentLlmCalls,
  useAgentTokens,
  useUpdateAgent,
} from './agentQueries';
import { TokenRevealDialog } from './TokenRevealDialog';

export function AgentDetailPage() {
  const { name = '' } = useParams();
  const agent = useAgent(name);

  return (
    <div className="min-h-0 flex-1 overflow-y-auto">
      <div className="mx-auto max-w-5xl space-y-6 p-4">
        <Link to="/agents" className="inline-flex min-h-11 items-center text-sm text-accent underline">
          Back to agents
        </Link>
        {agent.isPending ? (
          <LoadingState label="Loading agent" />
        ) : agent.isError ? (
          agent.error instanceof ApiError && agent.error.status === 404 ? (
            <div role="alert" className="rounded-lg border border-border p-4">
              <h1 className="text-xl font-semibold">Agent not found</h1>
              <p className="mt-1 text-sm">Not found. It may have been removed.</p>
            </div>
          ) : (
            <ErrorState message={errorMessage(agent.error)} onRetry={() => void agent.refetch()} />
          )
        ) : (
          <AgentDetail agent={agent.data} />
        )}
      </div>
    </div>
  );
}

function AgentDetail({ agent }: { agent: AgentDto }) {
  const update = useUpdateAgent();
  const toast = useToast();

  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="min-w-0 text-xl font-semibold break-all">{agent.name}</h1>
        <EnabledSwitch
          checked={agent.enabled}
          label={`Enabled: ${agent.name}`}
          disabled={update.isPending}
          onChange={(enabled) =>
            update.mutate(
              { name: agent.name, changes: { enabled } },
              { onError: (err) => toast(errorMessage(err), 'error') },
            )
          }
        />
      </div>
      <ConfigNote />
      <section
        aria-labelledby="agent-status"
        className="space-y-2 rounded-lg border border-border bg-surface p-4"
      >
        <h2 id="agent-status" className="font-semibold">
          Status
        </h2>
        <p className="text-sm">
          <span className="text-muted">Last seen: </span>
          <LastSeen iso={agent.lastSeenAt} />
        </p>
        <p className="text-sm">
          <span className="text-muted">Today: </span>
          {formatUsd(agent.todaySpendUsd)} of {formatUsd(agent.dailyBudgetUsd)}
        </p>
        <Meter
          value={agent.todaySpendUsd}
          max={agent.dailyBudgetUsd}
          label={`Today's spend for ${agent.name}`}
        />
        <p className="text-xs text-muted">resets at 00:00 UTC ({budgetResetLocalTime()} local)</p>
      </section>
      {/* Keyed so the form picks up new values after a save or an external change. */}
      <SettingsForm
        key={`${agent.model}|${agent.dailyBudgetUsd}|${agent.contextWindowTokens ?? ''}`}
        agent={agent}
      />
      <TokensSection name={agent.name} />
      <LlmCallsSection name={agent.name} />
    </>
  );
}

function SettingsForm({ agent }: { agent: AgentDto }) {
  const id = useId();
  const update = useUpdateAgent();
  const toast = useToast();
  const [model, setModel] = useState(agent.model);
  const [budget, setBudget] = useState(String(agent.dailyBudgetUsd));
  const [context, setContext] = useState(
    agent.contextWindowTokens == null ? '' : String(agent.contextWindowTokens),
  );
  const [errors, setErrors] = useState<{
    model?: string | null;
    budget?: string | null;
    context?: string | null;
  }>({});
  const [formError, setFormError] = useState<string | null>(null);

  function submit(e: FormEvent) {
    e.preventDefault();
    const found = {
      model: model.trim() ? null : 'Enter a model id.',
      budget: budgetError(budget),
      context: contextWindowError(context),
    };
    setErrors(found);
    setFormError(null);
    if (found.model || found.budget || found.context) return;
    update.mutate(
      {
        name: agent.name,
        changes: {
          model: model.trim(),
          dailyBudgetUsd: Number(budget),
          contextWindowTokens: context.trim() ? Number(context) : undefined,
        },
      },
      {
        onSuccess: () => toast('Saved.', 'success'),
        onError: (err) => setFormError(errorMessage(err)),
      },
    );
  }

  const modelId = `${id}-model`;
  const budgetId = `${id}-budget`;
  const contextId = `${id}-context`;

  return (
    <section aria-labelledby={`${id}-title`} className="rounded-lg border border-border bg-surface p-4">
      <h2 id={`${id}-title`} className="font-semibold">
        Settings
      </h2>
      <form onSubmit={submit} noValidate className="mt-3 grid gap-4 md:grid-cols-3">
        <Field id={modelId} label="Model" hint={MODEL_HINT} error={errors.model}>
          <input
            id={modelId}
            name="model"
            autoComplete="off"
            autoCapitalize="none"
            spellCheck={false}
            value={model}
            onChange={(e) => setModel(e.target.value)}
            className={inputClass}
            {...describedBy(modelId, true, errors.model)}
          />
        </Field>
        <Field id={budgetId} label="Daily budget (USD)" error={errors.budget}>
          <input
            id={budgetId}
            name="dailyBudgetUsd"
            type="number"
            inputMode="decimal"
            min={0}
            step="0.01"
            value={budget}
            onChange={(e) => setBudget(e.target.value)}
            className={inputClass}
            {...describedBy(budgetId, false, errors.budget)}
          />
        </Field>
        <Field
          id={contextId}
          label="Context window"
          hint={
            agent.contextWindowTokens == null
              ? 'Tokens, 1,000 to 2,000,000. Empty uses the default.'
              : 'Tokens, 1,000 to 2,000,000.'
          }
          error={errors.context}
        >
          <input
            id={contextId}
            name="contextWindowTokens"
            type="number"
            inputMode="numeric"
            min={CONTEXT_MIN}
            max={CONTEXT_MAX}
            step={1}
            placeholder="default"
            value={context}
            onChange={(e) => setContext(e.target.value)}
            className={inputClass}
            {...describedBy(contextId, true, errors.context)}
          />
        </Field>
        {formError && (
          <p role="alert" className="rounded-md bg-danger-soft p-2 text-sm md:col-span-3">
            {formError}
          </p>
        )}
        <div className="md:col-span-3">
          <Button type="submit" variant="primary" busy={update.isPending}>
            Save
          </Button>
        </div>
      </form>
    </section>
  );
}

function tokenStatus(token: TokenMetaDto) {
  if (token.revokedAt) return <Badge tone="danger">revoked</Badge>;
  if (token.expiresAt && Date.parse(token.expiresAt) <= Date.now()) return <Badge tone="warn">expired</Badge>;
  return <Badge tone="ok">active</Badge>;
}

function TokensSection({ name }: { name: string }) {
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

function LlmCallsSection({ name }: { name: string }) {
  const id = useId();
  const calls = useAgentLlmCalls(name);

  return (
    <section
      aria-labelledby={`${id}-title`}
      className="space-y-3 rounded-lg border border-border bg-surface p-4"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id={`${id}-title`} className="font-semibold">
          Recent LLM calls
        </h2>
        <Link
          to={`/audit?tab=llm&agent=${encodeURIComponent(name)}`}
          className="inline-flex min-h-11 items-center text-sm text-accent underline"
        >
          View in Audit
        </Link>
      </div>
      {calls.isPending ? (
        <LoadingState label="Loading LLM calls" />
      ) : calls.isError ? (
        <ErrorState message={errorMessage(calls.error)} onRetry={() => void calls.refetch()} />
      ) : calls.data.length === 0 ? (
        <EmptyState title="No LLM calls yet." />
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[40rem] text-left text-sm">
            <caption className="sr-only">The last 20 LLM calls for {name}</caption>
            <thead className="text-muted">
              <tr>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Time
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Model
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Status
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Tokens in/out
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Cost
                </th>
                <th scope="col" className="py-2 font-medium">
                  Latency
                </th>
              </tr>
            </thead>
            <tbody>
              {calls.data.map((c) => (
                <tr key={c.id} className="border-t border-border">
                  <td className="py-2 pr-3">
                    <RelativeTime iso={c.createdAt} />
                  </td>
                  <td className="py-2 pr-3 break-all">{c.model}</td>
                  <td className="py-2 pr-3">
                    <Badge
                      tone={
                        c.status === 'ok'
                          ? 'ok'
                          : c.status === 'cancelled'
                            ? 'neutral'
                            : c.status === 'rejected'
                              ? 'warn'
                              : 'danger'
                      }
                    >
                      {c.status}
                    </Badge>
                  </td>
                  <td className="py-2 pr-3">
                    {c.promptTokens} / {c.completionTokens}
                  </td>
                  <td className="py-2 pr-3">{formatCost(c.costUsd)}</td>
                  <td className="py-2">{formatDuration(c.latencyMs)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
