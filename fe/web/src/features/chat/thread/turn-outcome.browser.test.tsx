import '../../../styles/entry.css';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { CurrentStatusNotice } from './outcome-notice.tsx';

afterEach(cleanup);
it.each([390, 1280])('renders only lastTurn result with optional diagnostics (%ipx)', async width => {
  await page.viewport(width, 844);
  const turn = { id: 'turn', sessionId: 'session', turnIndex: 1, epoch: 1, triggerKind: 'user',
    status: 'failed', startedAtMs: 1000, completedAtMs: 9000, errorText: '请求失败，请稍后重试。' };
  const view = render(<CurrentStatusNotice lastTurn={turn} copyAction={null} regenerateAction={null} />);
  await userEvent.click(screen.getByRole('button', { name: /本轮执行失败/ }));
  expect(screen.getByText(turn.errorText).checkVisibility()).toBe(true);
  expect(document.querySelectorAll('[data-nc-current-meta]')).toHaveLength(1);
  view.rerender(<CurrentStatusNotice lastTurn={{ ...turn, status: 'unknown', errorText: null }} copyAction={null} regenerateAction={null} />);
  expect(screen.getByText('本轮结果未确认').checkVisibility()).toBe(true);
  expect(screen.queryByRole('button', { name: /重连|重置/ })).toBeNull();
  expect(document.documentElement.scrollWidth).toBe(width);
});
