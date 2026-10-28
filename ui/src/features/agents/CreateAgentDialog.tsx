import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useId, useState, type FormEvent } from 'react';
import { api, call } from '../../api/client';
import { ApiError, errorMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import { AGENT_NAME_PATTERN } from '../../api/types';
import { Button, Dialog } from '../../components/ui';
import { describedBy, Field } from './AgentParts';
import {
  budgetError,
  CONTEXT_MAX,
  CONTEXT_MIN,
  contextWindowError,
  inputClass,
  MODEL_HINT,
  NAME_HINT,
  SECRET_MUTATION,
} from './agentQueries';

interface Errors {
  name?: string | null;
  model?: string | null;
  budget?: string | null;
  context?: string | null;
  form?: string | null;
}

/**
 * Creates an agent. On success the token value is handed straight to `onCreated` (which opens the
 * reveal dialog) and the mutation is reset so its result does not linger anywhere.
 */
export function CreateAgentDialog({
  open,
  onOpenChange,
  onCreated,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onCreated: (tokenValue: string) => void;
}) {
  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title="New agent"
      description="Creates an agent and its first token."
    >
      {/* Mounted only while open, so the form starts empty each time. */}
      {open && <CreateAgentForm onCancel={() => onOpenChange(false)} onCreated={onCreated} />}
    </Dialog>
  );
}

function CreateAgentForm({
  onCancel,
  onCreated,
}: {
  onCancel: () => void;
  onCreated: (tokenValue: string) => void;
}) {
  const queryClient = useQueryClient();
  const id = useId();
  const [name, setName] = useState('');
  const [model, setModel] = useState('');
  const [budget, setBudget] = useState('1');
  const [context, setContext] = useState('');
  const [errors, setErrors] = useState<Errors>({});

  const create = useMutation({
    ...SECRET_MUTATION,
    mutationFn: (body: {
      name: string;
      model: string;
      dailyBudgetUsd: number;
      contextWindowTokens?: number;
    }) => call(() => api.POST('/api/agents', { body })),
  });

  function validate(): Errors {
    return {
      name: AGENT_NAME_PATTERN.test(name) ? null : NAME_HINT,
      model: model.trim() ? null : 'Enter a model id.',
      budget: budgetError(budget),
      context: contextWindowError(context),
    };
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    const found = validate();
    setErrors(found);
    if (found.name || found.model || found.budget || found.context) return;
    create.mutate(
      {
        name,
        model: model.trim(),
        dailyBudgetUsd: Number(budget),
        contextWindowTokens: context.trim() ? Number(context) : undefined,
      },
      {
        onSuccess: (result) => {
          void queryClient.invalidateQueries({ queryKey: keys.agents, exact: true });
          onCreated(result.token.value);
          create.reset();
        },
        onError: (err) => {
          if (err instanceof ApiError && err.code === 'conflict') setErrors({ name: 'Already exists.' });
          else setErrors({ form: errorMessage(err) });
        },
      },
    );
  }

  const nameId = `${id}-name`;
  const modelId = `${id}-model`;
  const budgetId = `${id}-budget`;
  const contextId = `${id}-context`;

  return (
    <form onSubmit={submit} noValidate className="space-y-4">
      <Field id={nameId} label="Name" hint={NAME_HINT} error={errors.name}>
        <input
          id={nameId}
          name="name"
          autoComplete="off"
          autoCapitalize="none"
          spellCheck={false}
          required
          value={name}
          onChange={(e) => setName(e.target.value)}
          className={inputClass}
          {...describedBy(nameId, true, errors.name)}
        />
      </Field>
      <Field id={modelId} label="Model" hint={MODEL_HINT} error={errors.model}>
        <input
          id={modelId}
          name="model"
          autoComplete="off"
          autoCapitalize="none"
          spellCheck={false}
          required
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
          required
          value={budget}
          onChange={(e) => setBudget(e.target.value)}
          className={inputClass}
          {...describedBy(budgetId, false, errors.budget)}
        />
      </Field>
      <Field
        id={contextId}
        label="Context window (optional)"
        hint="Tokens, 1,000 to 2,000,000. Leave empty to use the default."
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
          value={context}
          onChange={(e) => setContext(e.target.value)}
          className={inputClass}
          {...describedBy(contextId, true, errors.context)}
        />
      </Field>
      {errors.form && (
        <p role="alert" className="rounded-md bg-danger-soft p-2 text-sm">
          {errors.form}
        </p>
      )}
      <div className="flex justify-end gap-2">
        <Button onClick={onCancel}>Cancel</Button>
        <Button type="submit" variant="primary" busy={create.isPending}>
          Create agent
        </Button>
      </div>
    </form>
  );
}
