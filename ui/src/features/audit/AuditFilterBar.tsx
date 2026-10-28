import { useId, type ReactNode } from 'react';
import { TIME_WINDOWS, humanize, shortId } from './auditFormat';
import { type AuditFilters } from './auditParts';

export function Filters({
  statuses,
  filters,
  agentOptions,
  onChange,
}: {
  statuses: readonly string[];
  filters: AuditFilters;
  agentOptions: string[];
  onChange: (changes: Record<string, string | undefined>) => void;
}) {
  return (
    <div className="mt-4 flex flex-wrap items-end gap-3">
      <SelectField label="Agent" value={filters.agent ?? ''} onChange={(v) => onChange({ agent: v })}>
        <option value="">All agents</option>
        {agentOptions.map((name) => (
          <option key={name} value={name}>
            {name}
          </option>
        ))}
      </SelectField>
      <SelectField label="Status" value={filters.status ?? ''} onChange={(v) => onChange({ status: v })}>
        <option value="">All statuses</option>
        {statuses.map((s) => (
          <option key={s} value={s}>
            {humanize(s)}
          </option>
        ))}
      </SelectField>
      <SelectField label="Time window" value={filters.window ?? ''} onChange={(v) => onChange({ window: v })}>
        <option value="">All time</option>
        {TIME_WINDOWS.map((w) => (
          <option key={w.value} value={w.value}>
            {w.label}
          </option>
        ))}
      </SelectField>
      {filters.threadId && (
        <button
          type="button"
          onClick={() => onChange({ threadId: undefined })}
          aria-label={`Thread ${shortId(filters.threadId)}: remove thread filter`}
          className="inline-flex min-h-11 items-center gap-2 rounded-full border border-border bg-accent-soft px-4 text-sm"
        >
          Thread {shortId(filters.threadId)}
          <span aria-hidden="true">×</span>
        </button>
      )}
    </div>
  );
}

function SelectField({
  label,
  value,
  onChange,
  children,
}: {
  label: string;
  value: string;
  onChange: (value: string | undefined) => void;
  children: ReactNode;
}) {
  const id = useId();
  return (
    <div className="flex min-w-36 flex-1 flex-col gap-1 sm:flex-none">
      <label htmlFor={id} className="text-sm font-medium">
        {label}
      </label>
      <select
        id={id}
        value={value}
        onChange={(e) => onChange(e.target.value || undefined)}
        className="min-h-11 rounded-md border border-border bg-surface px-3 text-sm text-fg"
      >
        {children}
      </select>
    </div>
  );
}
