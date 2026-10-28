import { useId, useState, type FormEvent } from 'react';
import { errorMessage } from '../../api/errors';
import type { AgentDto } from '../../api/types';
import { useToast } from '../../app/toast';
import { Button } from '../../components/ui';
import { describedBy, Field } from './AgentParts';
import {
  budgetError,
  CONTEXT_MAX,
  CONTEXT_MIN,
  contextWindowError,
  inputClass,
  MODEL_HINT,
  useUpdateAgent,
} from './agentQueries';

export function SettingsForm({ agent }: { agent: AgentDto }) {
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
