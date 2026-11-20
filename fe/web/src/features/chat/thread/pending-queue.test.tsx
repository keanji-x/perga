// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { PendingQueue } from './pending-queue.tsx';
afterEach(cleanup);
it('renders queued text with steer and withdraw only', async () => {
  const onDelete = vi.fn(() => Promise.resolve({ kind: 'done' as const }));
  const onSteer = vi.fn(() => Promise.resolve({ kind: 'done' as const }));
  const entry = { entry_id: 'message', text: '检查报告', rev: 1, queued_at_ms: 1, state: 'waiting' as const };
  render(<PendingQueue entries={[entry]} pendingInputCount={0} overflow={0} busy={false}
    queueDisplayAccounting={{ displayed: [], suppressed: [] }} onDelete={onDelete} onSteer={onSteer} />);
  expect(screen.getByText('检查报告')).toBeTruthy();
  expect(screen.queryByRole('button', { name: /Edit/ })).toBeNull();
  await userEvent.click(screen.getByRole('button', { name: '立即插队' }));
  expect(onSteer).toHaveBeenCalledWith(entry);
  await userEvent.click(screen.getByRole('button', { name: '撤回' }));
  expect(onDelete).toHaveBeenCalledWith(entry);
});
it('hides the empty queue', () => {
  render(<PendingQueue entries={[]} pendingInputCount={0} overflow={0} busy={false}
    queueDisplayAccounting={{ displayed: [], suppressed: [] }} onDelete={() => Promise.resolve({ kind: 'done' })} />);
  expect(screen.queryByRole('region')).toBeNull();
});

it('allows withdrawing an unconfirmed input while keeping steer unavailable', async () => {
  const entry = { entry_id: 'unknown', text: 'Original unknown input', rev: 1, queued_at_ms: 1, state: 'unconfirmed' as const };
  const onDelete = vi.fn(() => Promise.resolve({ kind: 'done' as const }));
  render(<PendingQueue entries={[entry]} pendingInputCount={0} overflow={0} busy={false}
    queueDisplayAccounting={{ displayed: [], suppressed: [] }} onDelete={onDelete} onSteer={vi.fn()} />);
  expect(screen.queryByRole('button', { name: '立即插队' })).toBeNull();
  await userEvent.click(screen.getByRole('button', { name: '撤回' }));
  expect(onDelete).toHaveBeenCalledExactlyOnceWith(entry);
});
