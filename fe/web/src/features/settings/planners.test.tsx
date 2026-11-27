import userEvent from '@testing-library/user-event';
// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { PlannersPane, type PlannersPaneProps } from './planners.tsx';

afterEach(cleanup);

const PROVIDERS: NonNullable<PlannersPaneProps['providers']> = [
  { availability: { provider: 'codex', status: 'ready', reason: null, authentication_method: null, authentication_notice: null, checked_at_ms: 1_760_000_000_000 }, accepts_unavailable_blank_track: true },
  { availability: { provider: 'claude', status: 'not_configured', reason: 'Planner authentication is not configured',
    authentication_method: null, authentication_notice: null, checked_at_ms: 1_760_000_000_000 }, accepts_unavailable_blank_track: true },
];

function pane(overrides: Partial<PlannersPaneProps> = {}) {
  const props: PlannersPaneProps = {
    providers: PROVIDERS, loadError: null, onRetryLoad: vi.fn(), onRecheck: vi.fn(), rechecking: false,
    recheckError: null, authenticationRecovery: null,
    ...overrides,
  };
  const view = render(<PlannersPane {...props} />);
  return { ...props, rerender: view.rerender };
}

function row(title: string): HTMLElement {
  const item = screen.getByText(title).closest('li');
  if (item === null) throw new Error(`no row for ${title}`);
  return item;
}

describe('PlannersPane', () => {
  it.each(['xai.api_key', 'cached_token'])('shows the selected authentication method %s even when ready', (method) => {
    pane({ providers: [{ availability: { provider: 'grok', status: 'ready', reason: null,
      authentication_notice: null, authentication_method: method, checked_at_ms: 1 }, accepts_unavailable_blank_track: true }] });
    expect(within(row('Grok')).getByText(`Authentication: ${method}`)).toBeTruthy();
    expect(within(row('Grok')).getByText('Ready')).toBeTruthy();
  });

  it('says it is checking rather than guessing a status', () => {
    pane({ providers: undefined });
    expect(screen.getByText('Checking providers…')).toBeTruthy();
    expect(screen.queryByText('Ready')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Recheck planners' })).toBeNull();
  });

  it('shows each provider with its status, and the server reason when it is not ready', () => {
    pane();
    expect(within(row('Codex')).getByText('Ready')).toBeTruthy();
    expect(within(row('Codex')).queryByText(/Passed every check/)).toBeNull();
    expect(within(row('Claude')).getByText('Not configured')).toBeTruthy();
    expect(within(row('Claude')).getByText('Planner authentication is not configured')).toBeTruthy();
  });

  it('describes blank track creation during Codex and Claude outages (#3037)', () => {
    pane({ providers: [
      { availability: { provider: 'codex', status: 'unavailable', reason: 'shared codex app-server is not running', authentication_method: null, authentication_notice: null, checked_at_ms: 1 }, accepts_unavailable_blank_track: true },
      { availability: { provider: 'claude', status: 'unavailable', reason: 'not logged in', authentication_method: null, authentication_notice: null, checked_at_ms: 1 }, accepts_unavailable_blank_track: true },
    ] });
    expect(within(row('Codex')).getByText(
      'shared codex app-server is not running A track can still be created; it runs once Codex is back.',
    )).toBeTruthy();
    expect(within(row('Claude')).getByText('not logged in A track can still be created; it runs once Claude is back.')).toBeTruthy();
  });

  it.each([false, true])('uses injected blank-creation capability %s independently of provider identity', (acceptsUnavailable) => {
    pane({ providers: PROVIDERS.map(({ availability }) => ({
      availability: { ...availability, status: 'unavailable', reason: 'server outage' },
      accepts_unavailable_blank_track: acceptsUnavailable,
    })) });
    for (const title of ['Codex', 'Claude']) {
      expect(within(row(title)).getByText(acceptsUnavailable
        ? `server outage A track can still be created; it runs once ${title} is back.` : 'server outage')).toBeTruthy();
    }
  });

  it('retains server order, the oldest check and notice precedence over outage copy', () => {
    pane({ providers: [
      { ...PROVIDERS[1], availability: { provider: 'opencode', status: 'not_configured', reason: 'not configured', checked_at_ms: 3000, authentication_method: null, authentication_notice: null } },
      { ...PROVIDERS[0], availability: { provider: 'codex', status: 'unavailable', reason: 'daemon down', checked_at_ms: 1000,
        authentication_method: null, authentication_notice: { kind: 'refresh_error_reported', text: 'reported notice', revision: '9' } } },
    ] });
    expect(screen.getAllByRole('listitem').map((item) => within(item).queryByText(/^(OpenCode|Codex)$/)?.textContent)
      .filter(Boolean)).toEqual(['OpenCode', 'Codex']);
    expect(screen.getByText(`Last checked at ${new Date(1000).toLocaleTimeString()}`)).toBeTruthy();
    expect(within(row('Codex')).getByText('reported notice')).toBeTruthy();
    expect(screen.queryByText(/daemon down/)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retry queued messages after server sign-in' })).toBeNull();
  });

  it('names Grok and shows its sign-in advice when it is unavailable', () => {
    const reason = 'grok did not authenticate (authenticate `cached_token`: timed out); sign it in with its own CLI under the environment its ACP registration declares, then retry';
    pane({ providers: [
      { availability: { provider: 'grok', status: 'unavailable', reason, authentication_method: null, authentication_notice: null, checked_at_ms: 1 }, accepts_unavailable_blank_track: true },
    ] });
    expect(within(row('Grok')).getByText(`${reason} A track can still be created; it runs once Grok is back.`)).toBeTruthy();
  });

  it('rechecks on request and says when a recheck failed', () => {
    const props = pane({ recheckError: 'network down' });
    fireEvent.click(screen.getByRole('button', { name: 'Recheck planners' }));
    expect(props.onRecheck).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('alert').textContent).toBe('network down');
  });

  it('offers a retry when the first read failed', () => {
    const props = pane({ providers: undefined, loadError: 'boom' });
    fireEvent.click(screen.getByRole('button', { name: /retry/i }));
    expect(props.onRetryLoad).toHaveBeenCalledTimes(1);
  });
});

it('offers an injected owner retry only for a confirmed failure and passes its exact revision', async () => {
  const onRetry = vi.fn();
  pane({
    providers: [{ availability: { provider: 'codex', status: 'unavailable', reason: 'Sign in for the server.', checked_at_ms: 1,
      authentication_method: null, authentication_notice: { kind: 'sign_in_required', text: 'Sign in for the server.', revision: '7' } },
      accepts_unavailable_blank_track: true }],
    authenticationRecovery: { provider: 'codex', pending: false, error: null, notices: [], onRetry, onOpenConversation: vi.fn() },
  });
  await userEvent.click(screen.getByRole('button', { name: 'Retry queued messages after server sign-in' }));
  expect(onRetry).toHaveBeenCalledWith('7');
});
