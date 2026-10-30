import type { ReactNode } from 'react';

/** The dashboard card: a bold title on the left, a muted caption on the right, then the body. */
export function Card({
  title,
  caption,
  footer,
  children,
  className = '',
  titleId,
}: {
  title: ReactNode;
  caption?: ReactNode;
  footer?: ReactNode;
  children: ReactNode;
  className?: string;
  titleId?: string;
}) {
  return (
    <section
      aria-labelledby={titleId}
      className={`flex min-w-0 flex-col rounded-md border border-border bg-surface p-2.5 lcars:rounded-l-2xl lcars:rounded-r-none lcars:border-0 lcars:border-l-8 lcars:border-lcars-violet ${className}`}
    >
      <div className="mb-1 flex items-baseline justify-between gap-2">
        <h2
          id={titleId}
          className="text-sm font-semibold lcars:text-base lcars:font-normal lcars:text-accent"
        >
          {title}
        </h2>
        {caption && <span className="text-xs text-muted">{caption}</span>}
      </div>
      <div className="flex min-h-0 flex-1 flex-col">{children}</div>
      {footer && (
        <div className="mt-2 flex items-center justify-between border-t border-border pt-1.5 text-xs text-muted">
          {footer}
        </div>
      )}
    </section>
  );
}
