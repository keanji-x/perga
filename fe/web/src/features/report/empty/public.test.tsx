// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { ReportEmpty } from './public.tsx';

afterEach(cleanup);

it('offers the desktop fallback action and relies on the conversation dock on compact screens', async () => {
  const start = vi.fn();
  const { container, rerender } = render(<ReportEmpty state="blank" compactViewport={false} onStartConversation={start} />);

  expect(screen.getByRole('heading', { name: 'This page becomes your living report' })).toBeTruthy();
  expect(screen.getByText('Discuss goals and decisions with Lead; plans, findings, and progress will settle here.')).toBeTruthy();
  expect(container.querySelector('[data-nc-report-empty]')).toBeTruthy();
  expect(container.querySelector('[data-nc-report]')).toBeNull();
  expect(container.querySelector('[data-nc-activity]')).toBeNull();
  await userEvent.click(screen.getByRole('button', { name: 'Start with Lead' }));
  expect(start).toHaveBeenCalledOnce();

  rerender(<ReportEmpty state="blank" compactViewport onStartConversation={start} />);
  expect(screen.getByText(/Start typing below/)).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Start with Lead' })).toBeNull();
  expect(screen.queryByRole('list')).toBeNull();
});

it('lets real workspace, drafting, and conversation states determine the guidance', () => {
  const { container, rerender } = render(<ReportEmpty state="workspace-waiting" compactViewport={false} />);
  expect(container.firstChild).toBeNull();

  rerender(<ReportEmpty state="working" compactViewport={false} />);
  expect(screen.getByRole('heading', { name: 'Drafting the initial report…' })).toBeTruthy();
  expect(container.querySelector('[data-nc-activity="working"]')).toBeTruthy();

  rerender(<ReportEmpty state="conversing" compactViewport={false} />);
  expect(screen.getByRole('heading', { name: 'Nothing published to the report yet' })).toBeTruthy();
  expect(container.querySelector('[data-nc-activity]')).toBeNull();
});
