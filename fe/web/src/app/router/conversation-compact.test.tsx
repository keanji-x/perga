import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it } from 'vitest';
import { renderCompactFixture } from './conversation-compact-fixture.tsx';

afterEach(cleanup);

it.each(['claude', 'codex', 'opencode', 'grok'] as const)('compact is available for an existing v1 %s conversation', async provider => {
  const fixture = renderCompactFixture(provider);
  await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
  await screen.findByText('Earlier reply.');
  await userEvent.type(screen.getByRole('combobox', { name: 'Message' }), '/compact');
  await screen.findByRole('option', { name: /^compact/ });
  await userEvent.keyboard('{Enter}');
  await userEvent.keyboard('{Enter}');
  await waitFor(() => expect(fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/control'))).toHaveLength(1));
  expect(fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/messages'))).toHaveLength(0);
});

it('compact remains hidden until history is ready', async () => {
  renderCompactFixture('codex', false);
  await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
  await screen.findByText('无法加载对话记录。');
  await userEvent.type(screen.getByRole('combobox', { name: 'Message' }), '/');
  expect(screen.queryByRole('option', { name: /^compact/ })).toBeNull();
});

it('compact POST started and idle phase never imply success', async () => {
  const fixture = renderCompactFixture('claude', true);
  await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
  await screen.findByText('Earlier reply.');
  await userEvent.type(screen.getByRole('combobox', { name: 'Message' }), '/compact');
  await screen.findByRole('option', { name: /^compact/ });
  await userEvent.keyboard('{Enter}');
  await userEvent.keyboard('{Enter}');
  await waitFor(() => expect(fixture.requests.some(request => request.path.endsWith('/control'))).toBe(true));
  expect(screen.queryByText('已压缩')).toBeNull();
});

it('compact refusal surfaces an error and keeps the old valid reading', async () => {
  const fixture = renderCompactFixture('claude', true, { status: 409, statusText: 'Conflict', body: { error: 'Wait for pending messages and edits to finish before compacting.' } });
  fixture.run.session.contextUsage = { usedTokens: 120_320, windowTokens: 256_000, observedAtMs: 1, ratio: 0.47 };
  await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
  await screen.findByText('Earlier reply.');
  await userEvent.type(screen.getByRole('combobox', { name: 'Message' }), '/compact');
  await screen.findByRole('option', { name: /^compact/ });
  await userEvent.keyboard('{Enter}');
  await userEvent.keyboard('{Enter}');
  await screen.findByText('Wait for pending messages and edits to finish before compacting.');
  expect(screen.queryByText('已压缩')).toBeNull();
  expect(screen.getByRole('img', { name: '120k of 256k in context' }).getAttribute('data-nc-context-ring')).toBe('47');
});
