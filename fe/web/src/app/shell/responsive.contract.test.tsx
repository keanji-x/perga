// @vitest-environment jsdom
import { act, cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { createReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';
import { renderDailyFixture } from '../router/daily-planner-fixture.tsx';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
function compactViewport() {
  vi.stubGlobal('matchMedia', vi.fn(() => ({
    matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn(),
  })));
}

it('uses the shell Back slot and keeps Today content reachable on compact widths', async () => {
  compactViewport();
  renderDailyFixture({ initial: '/?view=today' });
  await screen.findByRole('button', { name: 'Back to Focus' });
  expect(screen.queryByRole('button', { name: 'Open workspace' })).toBeNull();
  expect(screen.getByRole('main').hasAttribute('inert')).toBe(false);
  await userEvent.click(screen.getByRole('button', { name: 'Back to Focus' }));
  expect(await screen.findByRole('button', { name: 'Open workspace' })).toBeTruthy();
});

it('reads the same receipt as the desktop rail and updates the mounted Area list', async () => {
  compactViewport();
  const receipts = createReadReceiptsKernel({ baselineMs: 100 });
  renderDailyFixture({ initial: '/area/project-area', receipts, reply: (request) =>
    request.path.startsWith('/api/v1/overlays?') ? {
      status: 200, statusText: 'OK', body: { tracks: [{
        trackId: 'project', areaId: 'project-area', isWorking: false, openAskCount: 0,
        pendingApprovalCount: 0, hasBlockedSession: false, unreadMailCount: 0,
        workspaceFailed: false, updatedAtMs: 150, notifiableSessions: [{ sessionId: 's1', lastNotifiableTurnAtMs: 150 }],
      }] },
    } : undefined,
  });
  const openTracks = await screen.findByRole('region', { name: 'Open tracks' });
  await within(openTracks).findByRole('button', { name: /^Project evidence.*Unread update$/ });
  await receipts.transport.send({
    method: 'POST', path: '/api/v1/read-receipts/conversation/s1/read', credentials: 'include', body: { readThroughMs: 150 },
  });
  act(() => { window.dispatchEvent(new Event('visibilitychange')); });
  await waitFor(() => expect(openTracks.textContent).toContain('No updates yet'));
});
