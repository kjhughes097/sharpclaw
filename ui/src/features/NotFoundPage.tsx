import { Link } from 'react-router';

export function NotFoundPage() {
  return (
    <div className="m-auto max-w-md p-6 text-center">
      <h1 className="text-xl font-semibold">Page not found</h1>
      <p className="mt-2 text-sm text-muted">That address doesn&apos;t match anything in SharpClaw.</p>
      <Link to="/overview" className="mt-4 inline-flex min-h-11 items-center text-accent underline">
        Go to the overview
      </Link>
    </div>
  );
}
