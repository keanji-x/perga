import '../../../styles/entry.css';
import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { CurrentStatusNotice } from './outcome-notice.tsx';
afterEach(() => { cleanup(); vi.useRealTimers(); });
it('ticks only a running lastTurn and uses its recorded completion duration', () => {
  vi.useFakeTimers(); vi.setSystemTime(100_000);
  const turn = { id: 'turn', sessionId: 'session', turnIndex: 1, epoch: 1, triggerKind: 'user', status: 'running', startedAtMs: 82_000, completedAtMs: null };
  const view = render(<CurrentStatusNotice lastTurn={turn} copyAction={null} regenerateAction={null} />);
  expect(screen.getByRole('status', { name: 'Current response status' }).textContent).toContain('18s');
  act(() => { vi.advanceTimersByTime(1000); });
  expect(screen.getByRole('status', { name: 'Current response status' }).textContent).toContain('19s');
  view.rerender(<CurrentStatusNotice lastTurn={{ ...turn, status: 'completed', completedAtMs: 109_000 }} copyAction={null} regenerateAction={null} />);
  expect(screen.getByRole('status', { name: 'Current response status' }).textContent).toContain('完成');
  expect(screen.getByRole('status', { name: 'Current response status' }).textContent).toContain('27s');
  expect(vi.getTimerCount()).toBe(0);
});
