import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

afterEach(() => {
  cleanup();
  document.getElementById('root')?.remove();
});

it.each([1280, 390])('keeps a saved first message and an editable composer during setup at %ipx', async (width) => {
  await page.viewport(width, 850);
  const message = 'The first message is already saved.';
  let workspace: 'pending' | 'failed' | null = 'pending';
  let retries = 0;
  const fixture = renderDailyFixture({
    initial: '/area/project-area/new',
    reply: (request) => {
      const ok = (body: unknown) => ({ status: 200, statusText: 'OK', body });
      const session = {
        id: 'ses_lead', trackId: 'daily', isLead: true, title: 'Lead', provider: 'codex',
        status: 'queued', contextUsage: null, createdAtMs: 1, updatedAtMs: 1,
      };
      if (request.path === '/api/v1/tracks/daily' || request.path === '/api/v1/tracks' && request.method === 'POST') return ok({
        track: { id: 'daily', areaId: 'project-area', title: 'Real Track', leadSessionId: session.id, createdAtMs: 1, updatedAtMs: 1 },
        report: {}, sessions: [session], workspacePreparation: workspace,
      });
      if (request.method === 'POST' && request.path === '/api/v1/sessions/ses_lead/messages') return ok({
        sessionId: session.id, inputId: 'inp_first', queued: true, delivery: 'queued',
      });
      if (request.path.startsWith('/api/v1/sessions/ses_lead/items?')) return ok({
        session, activeTurn: null, state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null,
        items: [{
          id: 1, sessionId: session.id, turnId: 'turn_first', kind: 'user', createdAtMs: 1,
          payload: { content: message, inputId: 'inp_first' },
        }],
      });
      if (request.path === '/api/v1/tracks/daily/workspace/retry') {
        retries += 1;
        workspace = null;
        return { status: 202, statusText: 'Accepted', body: undefined };
      }
      return undefined;
    },
  });
  await page.getByRole('combobox', { name: 'What this track should do' }).fill(message);
  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.poll(() => fixture.router.state.location.pathname).toMatch(/\/track\/daily$/);
  await expect.element(page.getByText('Preparing the workspace.')).toBeVisible();

  await expect.element(page.getByText(message, { exact: true })).toBeVisible();
  const composer = page.getByRole('combobox', { name: 'Message' });
  await composer.fill('A follow-up draft');
  await expect.element(composer).toHaveTextContent('A follow-up draft');
  await expect.element(page.getByRole('button', { name: 'Send', exact: true })).toBeDisabled();
  expect(fixture.router.state.location.pathname).toMatch(/\/track\/daily$/);

  workspace = 'failed';
  await page.getByRole('button', { name: 'Retry workspace setup' }).click();
  await expect.poll(() => retries).toBe(1);
  await expect.element(page.getByText(message, { exact: true })).toBeVisible();
  await expect.element(composer).toHaveTextContent('A follow-up draft');
});
