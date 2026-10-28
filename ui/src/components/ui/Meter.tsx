export function Meter({ value, max, label }: { value: number; max: number; label: string }) {
  const pct = max > 0 ? Math.min(100, Math.round((value / max) * 100)) : 0;
  return (
    <div
      role="meter"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={max}
      aria-valuenow={Math.min(value, max)}
      className="h-1.5 w-full overflow-hidden rounded-full bg-surface-2"
    >
      {/* React applies `style` through the CSSOM, which the CSP allows (unlike style attributes in markup). */}
      <div
        className={`h-full ${pct >= 90 ? 'bg-danger' : pct >= 70 ? 'bg-warn' : 'bg-accent'}`}
        style={{ width: `${pct}%` }}
      />
    </div>
  );
}
