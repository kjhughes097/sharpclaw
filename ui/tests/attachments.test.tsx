import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it } from 'vitest';
import type { components } from '../src/api/schema';
import { clearDraftsForTests } from '../src/features/chat/Composer';
import { fixtures, server } from './msw';
import { renderApp } from './utils';

type S = components['schemas'];
type Posted = { content: string; attachments?: { fileName: string; data: string }[] | null };
const T = 'thread-1';

function useThread(messages: S['MessageDto'][] = []) {
  const state = { posts: [] as Posted[], downloads: [] as string[] };
  server.use(
    http.get('*/api/threads/:id', () =>
      HttpResponse.json({
        thread: {
          id: T,
          agentId: 'agent-1',
          title: 'Files',
          createdAt: '2026-10-25T10:00:00Z',
          archived: false,
          lastMessageAt: '2026-10-25T11:00:00Z',
          context: { droppedTurns: 0, estimatedTokens: 100, windowTokens: 32000 },
        },
        agent: fixtures.agent(),
        inFlight: null,
        queued: 0,
        pendingApprovals: [],
      }),
    ),
    http.get('*/api/threads/:id/messages', () => HttpResponse.json({ messages, hasMoreBefore: false })),
    http.get('*/api/threads/:id/trace', () => HttpResponse.json({ turns: [] })),
    http.get('*/api/threads', () => HttpResponse.json({ threads: [fixtures.thread()], nextCursor: null })),
    http.get('*/api/threads/:id/attachments/:attachmentId', ({ params }) => {
      state.downloads.push(params.attachmentId as string);
      return new HttpResponse(new Uint8Array([104, 105]), { headers: { 'Content-Type': 'text/plain' } });
    }),
    http.post('*/api/threads/:id/messages', async ({ request }) => {
      state.posts.push((await request.json()) as Posted);
      return HttpResponse.json(
        {
          message: {
            id: `posted-${state.posts.length}`,
            status: 'pending',
            createdAt: '2026-10-25T12:00:00Z',
          },
        },
        { status: 202 },
      );
    }),
  );
  return state;
}

const png = () => new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], 'shot.png', { type: 'image/png' });
const md = () => new File(['# notes'], 'notes.md', { type: 'text/markdown' });
const b64 = (text: string) => btoa(text);

function drop(target: HTMLElement, files: File[]) {
  fireEvent.drop(target, { dataTransfer: { files, types: ['Files'] } });
}

beforeEach(() => clearDraftsForTests());

describe('composer attachments', () => {
  it('picks files, shows them as chips and sends them base64 with the text', async () => {
    const state = useThread();
    const { user } = renderApp(`/prompts/${T}`);
    await screen.findByLabelText('Message');

    await user.upload(screen.getByTestId('attachment-input'), [png(), md()]);
    const chips = await screen.findByRole('list', { name: 'Attachments' });
    expect(within(chips).getByText('shot.png')).toBeInTheDocument();
    expect(within(chips).getByText('notes.md')).toBeInTheDocument();

    await user.type(screen.getByLabelText('Message'), 'Read these');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(state.posts).toHaveLength(1));
    expect(state.posts[0]!).toEqual({
      content: 'Read these',
      unattended: false,
      attachments: [
        { fileName: 'shot.png', data: 'iVBORw==' },
        { fileName: 'notes.md', data: b64('# notes') },
      ],
    });
    await waitFor(() => expect(screen.queryByRole('list', { name: 'Attachments' })).not.toBeInTheDocument());
  });

  it('sends files without text, and Send stays disabled with neither', async () => {
    const state = useThread();
    const { user } = renderApp(`/prompts/${T}`);
    await screen.findByLabelText('Message');
    const sendButton = screen.getByRole('button', { name: 'Send' });
    expect(sendButton).toBeDisabled();

    await user.upload(screen.getByTestId('attachment-input'), md());
    await waitFor(() => expect(sendButton).toBeEnabled());
    await user.click(sendButton);

    await waitFor(() => expect(state.posts).toHaveLength(1));
    expect(state.posts[0]!.content).toBe('');
    expect(state.posts[0]!.attachments).toHaveLength(1);
  });

  it('removes a chip before sending', async () => {
    const state = useThread();
    const { user } = renderApp(`/prompts/${T}`);
    await screen.findByLabelText('Message');

    await user.upload(screen.getByTestId('attachment-input'), [png(), md()]);
    await user.click(await screen.findByRole('button', { name: 'Remove shot.png' }));
    await user.type(screen.getByLabelText('Message'), 'x');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(state.posts).toHaveLength(1));
    expect(state.posts[0]!.attachments?.map((a) => a.fileName)).toEqual(['notes.md']);
  });

  it('accepts dropped files and explains the ones it rejects', async () => {
    useThread();
    renderApp(`/prompts/${T}`);
    const box = await screen.findByLabelText('Message');
    const big = new File([new Uint8Array(256 * 1024 + 1)], 'big.txt');

    drop(box.closest('form')!, [md(), new File(['x'], 'run.exe'), big, new File([], 'empty.md')]);

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent("run.exe: this file type can't be attached.");
    expect(alert).toHaveTextContent('big.txt is larger than 256 KB.');
    expect(alert).toHaveTextContent('empty.md is empty.');
    const chips = screen.getByRole('list', { name: 'Attachments' });
    expect(within(chips).getAllByRole('listitem')).toHaveLength(1);
  });

  it('allows at most five files', async () => {
    useThread();
    renderApp(`/prompts/${T}`);
    const box = await screen.findByLabelText('Message');
    const files = Array.from({ length: 6 }, (_, i) => new File(['x'], `f${i}.txt`));

    drop(box.closest('form')!, files);

    expect(await screen.findByRole('alert')).toHaveTextContent('f5.txt: at most 5 files per message.');
    expect(within(screen.getByRole('list', { name: 'Attachments' })).getAllByRole('listitem')).toHaveLength(
      5,
    );
    expect(screen.getByRole('button', { name: 'Attach files' })).toBeDisabled();
  });

  it('turns a pasted image into an attachment with a distinct name', async () => {
    useThread();
    renderApp(`/prompts/${T}`);
    const box = await screen.findByLabelText('Message');
    const image = new File([new Uint8Array([1])], 'image.png', { type: 'image/png' });

    act(() => {
      fireEvent.paste(box, { clipboardData: { files: [image], getData: () => '' } });
    });

    const chips = await screen.findByRole('list', { name: 'Attachments' });
    expect(within(chips).getByText(/^pasted-.*\.png$/)).toBeInTheDocument();
  });
});

describe('sent attachments', () => {
  const sent = fixtures.message({
    id: 'u1',
    seq: 1,
    content: 'see attached',
    attachments: [
      { id: 'a1', fileName: 'shot.png', kind: 'image', mediaType: 'image/png', sizeBytes: 2048 },
      { id: 'a2', fileName: 'notes.md', kind: 'text', mediaType: 'text/plain; charset=utf-8', sizeBytes: 10 },
    ],
  });

  it('shows images as thumbnails and text files as download links', async () => {
    useThread([sent]);
    renderApp(`/prompts/${T}`);

    const list = await screen.findByRole('list', { name: 'Attached files' });
    const img = within(list).getByRole('img', { name: 'shot.png' });
    expect(img).toHaveAttribute('src', `/api/threads/${T}/attachments/a1`);
    const link = within(list).getByRole('link', { name: /notes\.md \(10 B\)/ });
    expect(link).toHaveAttribute('href', `/api/threads/${T}/attachments/a2`);
    expect(link).toHaveAttribute('download', 'notes.md');
  });

  it('Resend posts the failed message again with its files', async () => {
    const state = useThread([
      { ...sent, status: 'failed', failReason: 'provider_error', attachments: [sent.attachments![1]!] },
    ]);
    const { user } = renderApp(`/prompts/${T}`);

    await user.click(await screen.findByRole('button', { name: 'Resend' }));

    await waitFor(() => expect(state.posts).toHaveLength(1));
    expect(state.downloads).toEqual(['a2']);
    expect(state.posts[0]!.attachments).toEqual([{ fileName: 'notes.md', data: b64('hi') }]);
  });

  it('has no accessibility violations with chips and attachments', async () => {
    useThread([sent]);
    const { user, container } = renderApp(`/prompts/${T}`);
    await screen.findByRole('list', { name: 'Attached files' });
    await user.upload(screen.getByTestId('attachment-input'), md());
    await screen.findByRole('list', { name: 'Attachments' });
    expect(await axe(container)).toHaveNoViolations();
  });
});
