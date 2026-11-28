import { cleanup, fireEvent, render, within } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { userEvent } from 'vitest/browser';

import '../../../styles/entry.css';
import { ChatThread } from './public.tsx';
const RUNNING_TURN = Object.freeze({ id: 't1', sessionId: 'chat', turnIndex: 1, epoch: 1, triggerKind: 'user', status: 'running', startedAtMs: null });
import type { Conversation, ConversationActivity } from '../../../../../core/domain/conversation.ts';

afterEach(cleanup);

const conversation: Conversation = Object.freeze({ provider: 'codex' as const, id: 'motion', trackId: 'track', title: null, kind: 'codex', state: 'running', updatedAt: 1 });
function action(id: string, state: 'running' | 'done'): ConversationActivity {
  return { id, author: 'activity', tool: null, verb: state === 'running' ? 'Running' : 'Ran', target: 'npm test', state,
    durationMs: state === 'done' ? 1400 : null, detail: null, atMs: 1 };
}

it('shows the vendor spinner in a collapsed and expanded tool group and hides it when the turn ends', () => {
  const { container, rerender } = render(<ChatThread hasPendingMessages={false} canContinue={false} conversation={conversation} turns={[action('first', 'done'), action('second', 'running')]} lastSessionTurn={RUNNING_TURN} cards={{}} stalled={false} />);
  const status = container.querySelector<HTMLElement>('[data-nc-live] [role="progressbar"]')!;
  expect(status.checkVisibility()).toBe(true);
  fireEvent.click(container.querySelector('[aria-expanded]')!);
  expect(container.querySelector<HTMLElement>('[data-nc-live] [role="progressbar"]')!.checkVisibility()).toBe(true);
  rerender(<ChatThread hasPendingMessages={false} canContinue={false} conversation={conversation} turns={[action('first', 'done'), action('second', 'running')]} cards={{}} stalled={false} />);
  expect(container.querySelector('[data-nc-live]')).toBeNull();
  expect(container.querySelector('[role="progressbar"]')!.checkVisibility()).toBe(false);
});

it('replaces the running spinner with a visible completion icon', () => {
  const props = { hasPendingMessages: false, canContinue: false, conversation, cards: {}, stalled: false };
  const { container, rerender } = render(<ChatThread {...props} turns={[action('first', 'done'), action('second', 'running')]} lastSessionTurn={RUNNING_TURN} />);
  expect(container.querySelector('[data-nc-live] [role="progressbar"]')!.checkVisibility()).toBe(true);
  rerender(<ChatThread {...props} turns={[action('first', 'done'), action('second', 'done')]} />);
  const icon = container.querySelector('[aria-expanded="false"] > span:first-child')!;
  expect(icon.querySelector('svg')!.checkVisibility()).toBe(true);
  expect(icon.querySelector('svg')!.getBoundingClientRect().width).toBeGreaterThan(0);
  expect(container.querySelector('[role="progressbar"]')).toBeNull();
});

it('keeps the tool group toggle clickable through the running spinner', async () => {
  const { container } = render(<ChatThread hasPendingMessages={false} canContinue={false} conversation={conversation} turns={[action('first', 'done'), action('second', 'running')]} lastSessionTurn={RUNNING_TURN} cards={{}} stalled={false} />);
  await userEvent.click(container.querySelector<HTMLElement>('[role="progressbar"]')!);
  expect(container.querySelector('[aria-expanded="true"]')).not.toBeNull();
});

it('places the lone running spinner before the verb and centers it within the row', () => {
  const { container } = render(<ChatThread hasPendingMessages={false} canContinue={false} conversation={conversation} turns={[action('single', 'running')]} lastSessionTurn={RUNNING_TURN} cards={{}} stalled={false} />);
  const marker = container.querySelector<HTMLElement>('[data-nc-live] [role="progressbar"]')!;
  const row = container.querySelector<HTMLElement>('[data-nc-entry] > div')!;
  expect(marker.checkVisibility()).toBe(true);
  /* The painted verb, not Astryx's visually hidden `Running` status. */
  const verb = within(row).getAllByText('Running').find((element) => element.getBoundingClientRect().width > 1)!;
  const iconBox = marker.getBoundingClientRect();
  const textBox = verb.getBoundingClientRect();
  const rowBox = row.getBoundingClientRect();
  expect(container.querySelectorAll('[data-nc-live] [role="progressbar"]')).toHaveLength(1);
  expect(iconBox.right).toBeLessThanOrEqual(textBox.left);
  expect(iconBox.top + iconBox.height / 2).toBeCloseTo(rowBox.top + rowBox.height / 2, 1);
});
