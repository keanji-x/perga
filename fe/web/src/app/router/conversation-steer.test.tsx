import { act, cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it } from 'vitest';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { mountSteerFixture as mount, steerAnswer as answer } from './conversation-steer-fixture.tsx';

const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });
async function open() {
  await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
  await screen.findByText('Stored words');
}
function expectNoQueueControls() {
  for (const name of ['Say it now', 'Edit queued message', 'Delete this message']) {
    expect(screen.queryByRole('button', { name })).toBeNull();
  }
}

// v1 reports providers via BootstrapDto, but exposes no editable pending queue.
it.each(['codex', 'claude', 'opencode'] as const)(
  'does not invent queued steering controls from the %s bootstrap', async provider => {
    const fixture = mount(provider, () => ok(answer(provider)));
    await open();
    await waitFor(() => expect(fixture.client.getQueryData(['managed-providers'])).toMatchObject([
      { availability: { provider }, capabilities: { steer: true, history_replacement: false } },
    ]));
    expectNoQueueControls();
  });
it('keeps queue controls absent while bootstrap loads and after a stale-data query error', async () => {
  let release!: (response: ApiTransportResponse) => void;
  const managed = new Promise<ApiTransportResponse>(resolve => { release = resolve; });
  let failed = false;
  const fixture = mount('codex', () => failed ? { status: 503, statusText: 'Unavailable', body: { error: 'offline' } } : managed);
  await open();
  expectNoQueueControls();
  await act(async () => { release(ok(answer('codex'))); await managed; });
  await waitFor(() => expect(fixture.client.getQueryData(['managed-providers'])).toBeDefined());
  await act(async () => {
    failed = true;
    await fixture.client.refetchQueries({ queryKey: ['managed-providers'] });
  });
  expectNoQueueControls();
});
it('keeps persisted input readable when managed bootstrap fails before any answer', async () => {
  mount('codex', () => ({ status: 503, statusText: 'Unavailable', body: { error: 'offline' } }));
  await open();
  expectNoQueueControls();
});
