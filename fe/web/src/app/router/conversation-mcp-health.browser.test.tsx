import '../../styles/entry.css';
import { act, cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import type { SessionItemsPageDto } from '../../../../core/api/perga-v1.ts';
import { decodeEventFrame } from '../../../../core/events/protocol.ts';
import { EventStream, type EventStreamSink } from '../../systems/events/event-stream.ts';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionPage, sessionReply } from './conversation-session-data.ts';

afterEach(cleanup);

it('shows and clears the Lead MCP warning on runtime status events', async () => {
  await page.viewport(1280, 850);
  let sink: EventStreamSink | undefined;
  const stream = EventStream.create('ws://fixture.invalid', {
    start: (_config, _url, active) => { sink = active; },
    stop: () => { sink = undefined; },
  });
  let mcpHealth: SessionItemsPageDto['mcpHealth'] = null;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', stream, reply: request =>
    request.path.includes('/daily-planner/items?')
      ? sessionReply({ ...sessionPage(view.sessions[0]), mcpHealth })
      : undefined,
  });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const chip = page.getByText('Tools disconnected', { exact: true });
  const warning = page.getByText('Perga tools are not connected — Lead cannot update the Report or manage Flow.', { exact: true });
  await expect.element(page.getByRole('combobox', { name: 'Message' })).toHaveAttribute('contenteditable', 'true');
  await expect.element(chip).not.toBeInTheDocument();
  await expect.element(warning).not.toBeInTheDocument();

  function publishStatus() {
    const decoded = decodeEventFrame({ kind: 'session.runtime_status', trackId: 'daily', eventVersion: 1,
      payload: { sessionId: 'daily-planner', status: null } });
    if (decoded.status !== 'ready' || sink === undefined) throw new Error('Event stream was not ready');
    act(() => sink?.frame(decoded.frame));
  }

  mcpHealth = { status: 'failed', error: 'HTTP 502' };
  publishStatus();
  await expect.element(chip).toBeVisible();
  await expect.element(warning).toBeVisible();
  await expect.element(page.getByRole('alert').getByText('HTTP 502', { exact: true })).toBeVisible();

  mcpHealth = null;
  publishStatus();
  await expect.element(chip).not.toBeInTheDocument();
  await expect.element(warning).not.toBeInTheDocument();
});
