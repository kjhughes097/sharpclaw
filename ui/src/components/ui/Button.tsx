import { forwardRef, type ButtonHTMLAttributes } from 'react';
import { Spinner } from './Feedback';

type Variant = 'primary' | 'secondary' | 'danger' | 'ghost';

const VARIANTS: Record<Variant, string> = {
  primary: 'bg-accent text-accent-fg hover:opacity-90',
  secondary:
    'border border-border bg-surface text-fg hover:bg-surface-2 lcars:border-0 lcars:bg-lcars-blue lcars:text-accent-fg lcars:hover:bg-lcars-peach',
  danger: 'bg-danger text-surface hover:opacity-90',
  ghost: 'text-fg hover:bg-surface-2',
};

/** Touch targets are at least 44 px tall (3.3 spec section 9.10). */
export const Button = forwardRef<
  HTMLButtonElement,
  ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; busy?: boolean }
>(function Button({ variant = 'secondary', busy = false, className = '', children, disabled, ...rest }, ref) {
  return (
    <button
      ref={ref}
      type="button"
      className={`inline-flex min-h-11 items-center justify-center gap-2 rounded-md px-4 text-sm font-medium lcars:rounded-full lcars:uppercase lcars:tracking-wide disabled:cursor-not-allowed disabled:opacity-50 ${VARIANTS[variant]} ${className}`}
      disabled={disabled || busy}
      aria-busy={busy || undefined}
      {...rest}
    >
      {busy && <Spinner small />}
      {children}
    </button>
  );
});
