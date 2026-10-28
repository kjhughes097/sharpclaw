import { useRef, useState, type ComponentProps, type ReactNode } from 'react';
import ReactMarkdown, { type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { copyText, selectContents } from './clipboard';

/** Messages longer than this render as plain text first, so the page doesn't freeze. */
export const MAX_FORMATTED_CHARS = 200_000;

/**
 * Only http(s), mailto and in-page anchors survive; anything else (javascript:, data:, vbscript:,
 * relative paths…) becomes an empty href, which renders as plain text.
 */
export function safeUrl(url: string): string {
  const trimmed = url.trim();
  if (trimmed.startsWith('#')) return trimmed;
  try {
    const parsed = new URL(trimmed);
    if (parsed.protocol === 'http:' || parsed.protocol === 'https:' || parsed.protocol === 'mailto:')
      return trimmed;
  } catch {
    // Not absolute: not allowed.
  }
  return '';
}

function CodeBlock({ children, ...rest }: ComponentProps<'pre'>) {
  const ref = useRef<HTMLPreElement>(null);
  const [status, setStatus] = useState<'idle' | 'copied' | 'failed'>('idle');
  return (
    <div className="relative my-2">
      <pre
        ref={ref}
        {...rest}
        className="overflow-x-auto rounded-md border border-border bg-surface-2 p-3 pr-16 font-mono text-sm"
      >
        {children}
      </pre>
      <button
        type="button"
        className="absolute top-1 right-1 min-h-11 rounded px-2 text-xs text-muted hover:bg-surface"
        onClick={() => {
          const text = ref.current?.textContent ?? '';
          const ok = copyText(text);
          setStatus(ok ? 'copied' : 'failed');
          if (!ok) selectContents(ref.current);
        }}
      >
        {status === 'copied' ? 'Copied' : status === 'failed' ? 'Selected' : 'Copy'}
      </button>
    </div>
  );
}

const components: Components = {
  a({ href, children }) {
    if (!href) return <span>{children}</span>;
    if (href.startsWith('#')) return <a href={href}>{children}</a>;
    return (
      <a href={href} target="_blank" rel="noopener noreferrer nofollow" title={href}>
        {children}
      </a>
    );
  },
  // Remote images are never loaded: an image URL can carry data out (prompt injection), so it
  // becomes a text link the user can choose to open. The CSP (img-src 'self' data:) backs this up.
  img({ src, alt }) {
    const url = typeof src === 'string' ? safeUrl(src) : '';
    const label = `[image: ${alt || 'untitled'}]`;
    if (!url || url.startsWith('#')) return <span>{label}</span>;
    return (
      <a href={url} target="_blank" rel="noopener noreferrer nofollow" title={url}>
        {label}({url})
      </a>
    );
  },
  pre: CodeBlock,
  table({ children }) {
    return (
      <div className="my-2 overflow-x-auto">
        <table className="border-collapse text-sm">{children}</table>
      </div>
    );
  },
};

/**
 * Assistant text as GitHub-flavoured markdown with raw HTML disabled (`skipHtml`, no rehype-raw)
 * and links restricted (3.3 spec section 9.6).
 */
export function Markdown({ text }: { text: string }) {
  return (
    <div className="markdown break-words">
      <ReactMarkdown remarkPlugins={[remarkGfm]} skipHtml urlTransform={safeUrl} components={components}>
        {text}
      </ReactMarkdown>
    </div>
  );
}

/** Plain text with line breaks preserved: user messages, tool arguments and results. */
export function PlainText({ text, className = '' }: { text: string; className?: string }) {
  return <div className={`break-words whitespace-pre-wrap ${className}`}>{text}</div>;
}

/** Markdown, except very long messages start as plain text with a toggle. */
export function AssistantText({ text, footer }: { text: string; footer?: ReactNode }) {
  const [formatted, setFormatted] = useState(text.length <= MAX_FORMATTED_CHARS);
  return (
    <div>
      {formatted ? <Markdown text={text} /> : <PlainText text={text} />}
      {text.length > MAX_FORMATTED_CHARS && (
        <button
          type="button"
          className="mt-1 min-h-11 text-xs text-accent underline"
          onClick={() => setFormatted((f) => !f)}
        >
          {formatted ? 'Show as plain text' : 'Render formatting'}
        </button>
      )}
      {footer}
    </div>
  );
}
