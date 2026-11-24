// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { ThreadStatusNotice } from './status-notice.tsx';
import { ChatThread } from './public.tsx';

afterEach(cleanup);
it('copies the visible response Markdown only after a deliberate click', async () => {
  const copyText = vi.fn().mockResolvedValue(undefined);
  const text = 'Reply\n\n```ts\nx()\n```';
  render(<ChatThread lastSessionTurn={{ id: 'turn', sessionId: 'c', turnIndex: 1, epoch: 1, triggerKind: 'user', status: 'completed', startedAtMs: null }} hasPendingMessages={false} conversation={{ provider: 'codex' as const, id: 'c', trackId: 't', title: null, kind: 'codex', state: 'idle', updatedAt: 0 }} cards={{}}
    stalled={false} canContinue={false} copyText={copyText} turns={[
      { id: 'u', author: 'you', text: 'Prompt', atMs: 1 }, { id: 'a', author: 'agent', text, atMs: 2 },
      { id: 'end', author: 'turn', elapsedMs: null, turnId: 'turn', status: 'completed', atMs: 3 },
    ]} />);
  expect(copyText).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Copy reply' }));
  await screen.findByRole('button', { name: 'Copied reply' });
  expect(copyText).toHaveBeenCalledExactlyOnceWith(text);
});
it('reports rejected copy without fabricating success, in a fixed sentence and never the rejection text', async () => {
  render(<ThreadStatusNotice heading="Completed" clock={{ elapsedMs: null, timestamp: null }}
    copyAction={{ id: 'a', text: 'text', run: () => Promise.reject(new Error('Permission denied')) }} />);
  fireEvent.click(screen.getByRole('button', { name: 'Copy reply' }));
  await screen.findByRole('button', { name: 'Copy failed' });
  expect(screen.queryByRole('button', { name: 'Copied reply' })).toBeNull();
  expect(screen.queryByRole('button', { name: /Permission denied/ })).toBeNull();
});
it('ignores old copy completion when the response changes', async () => {
  let resolve!: () => void;
  const pending = new Promise<void>((done) => { resolve = done; });
  const view = (id: string) => <ThreadStatusNotice heading="Completed" clock={{ elapsedMs: null, timestamp: null }}
    copyAction={{ id, text: id, run: id === 'a' ? () => pending : () => Promise.resolve() }} />;
  const { rerender } = render(view('a'));
  fireEvent.click(screen.getByRole('button', { name: 'Copy reply' }));
  rerender(view('b')); resolve();
  await waitFor(() => expect(screen.getByRole('button', { name: 'Copy reply' })).toBeTruthy());
  expect(screen.queryByRole('button', { name: 'Copied reply' })).toBeNull();
});

it('does not let a hung old copy block the new response or overwrite its success', async () => {
  let resolveOld!: () => void;
  const old = new Promise<void>((done) => { resolveOld = done; });
  const copy = vi.fn<(text: string) => Promise<void>>().mockImplementationOnce(() => old).mockResolvedValue(undefined);
  const view = (id: string) => <ThreadStatusNotice heading="Completed" clock={{ elapsedMs: null, timestamp: null }}
    copyAction={{ id, text: id, run: () => copy(id) }} />;
  const { rerender } = render(view('a'));
  fireEvent.click(screen.getByRole('button', { name: 'Copy reply' }));
  rerender(view('b'));
  fireEvent.click(screen.getByRole('button', { name: 'Copy reply' }));
  await screen.findByRole('button', { name: 'Copied reply' });
  resolveOld();
  await waitFor(() => expect(copy.mock.calls).toEqual([['a'], ['b']]));
  expect(screen.getByRole('button', { name: 'Copied reply' })).toBeTruthy();
});

it('regenerates only the delivered prompt after an explicit click, including its images', async () => {
  const regenerate = vi.fn().mockResolvedValue(undefined);
  const user = { id: 'u', author: 'you' as const, text: 'Prompt', atMs: 1, attachments: [
    { id: 'image', contentType: 'image/png', size: 4, url: '/image' },
  ] };
  render(<ChatThread lastSessionTurn={{ id: 'turn', sessionId: 'c', turnIndex: 1, epoch: 1, triggerKind: 'user', status: 'completed', startedAtMs: null }} hasPendingMessages={false} conversation={{ provider: 'codex' as const, id: 'c', trackId: 't', title: null, kind: 'codex', state: 'idle', updatedAt: 0 }} cards={{}}
    stalled={false} canContinue={false} regenerateMessage={regenerate} turns={[
      user, { id: 'a', author: 'agent', text: 'Answer', atMs: 2 },
      { id: 'end', author: 'turn', elapsedMs: null, turnId: 'turn', status: 'completed', atMs: 3 },
    ]} />);
  expect(regenerate).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Regenerate' }));
  await waitFor(() => expect(regenerate).toHaveBeenCalledExactlyOnceWith(user));
  expect(screen.getByText('Answer', { exact: true })).toBeTruthy();
});

const EDIT_CONVERSATION = { provider: 'codex' as const, id: 'c', trackId: 't', title: null, kind: 'codex' as const, state: 'idle' as const, updatedAt: 0 };
const editTurns = (first: 'you' | 'system' = 'you') => [
  first === 'you' ? { id: 'u', author: 'you' as const, text: 'Prompt', atMs: 1 }
    : { id: 'wake', author: 'system' as const, label: 'Wake', text: 'Automatic turn', atMs: 1 },
  { id: 'a', author: 'agent' as const, text: 'Answer', atMs: 2 },
  { id: 'end', author: 'turn' as const, turnId: 'turn-7', status: 'completed' as const, atMs: 3, elapsedMs: null },
];

it('marks the messages of the turn being edited and leaves the rest alone', () => {
  render(<ChatThread lastSessionTurn={{ id: 'turn', sessionId: 'c', turnIndex: 1, epoch: 1, triggerKind: 'user', status: 'completed', startedAtMs: null }} hasPendingMessages={false} conversation={EDIT_CONVERSATION} cards={{}} stalled={false} canContinue={false}
    editing="end" turns={[{ id: 'u0', author: 'you', text: 'Earlier', atMs: 0 }, { id: 'o0', author: 'turn', turnId: 'turn-6', status: 'completed', elapsedMs: null, atMs: 0 },
      ...editTurns()]} />);
  expect(Array.from(document.querySelectorAll('[data-nc-editing]')).map((said) => said.textContent)).toEqual(['Prompt']);
  expect(screen.getByText('Answer', { exact: true })).toBeTruthy();
});

it('omits editing controls for a settled response', () => {
  render(<ChatThread hasPendingMessages={false} conversation={EDIT_CONVERSATION} cards={{}} stalled={false} canContinue={false}
    lastSessionTurn={{ id: 'turn', sessionId: 'c', turnIndex: 1, epoch: 1, triggerKind: 'user', status: 'completed', startedAtMs: null }} turns={editTurns()} />);
  expect(screen.queryByRole('button', { name: /Edit/ })).toBeNull();
});
