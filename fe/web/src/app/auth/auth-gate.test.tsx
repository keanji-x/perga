// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { ApiTransportPort, ApiRequest } from '../../../../core/api/types.ts';
import { AuthGate } from './auth-gate.tsx';

afterEach(cleanup);

it('returns to login when another setup request wins', async () => {
  const send = vi.fn<ApiTransportPort['send']>()
    .mockResolvedValueOnce({ status: 200, statusText: 'OK', body: {
      mode: 'setup_required', needsOwnerSetup: true, canSetupInBrowser: true, setupBlockedReason: null, nextStep: null,
    } })
    .mockResolvedValueOnce({ status: 409, statusText: 'Conflict', body: {
      code: 'owner_already_configured', error: 'The owner already exists.',
    } });
  const reload = vi.fn();
  render(<AuthGate transport={{ send }} reload={reload} />);
  expect(await screen.findByRole('heading', { name: 'Create your account' })).toBeTruthy();
  expect(screen.getByLabelText<HTMLInputElement>('Username').value).toBe('owner');
  const password = screen.getByLabelText('Password', { exact: true });
  expect(password.getAttribute('autocomplete')).toBe('new-password');
  fireEvent.click(screen.getByRole('button', { name: 'Show password' }));
  expect(password.getAttribute('type')).toBe('text');
  fireEvent.change(password, { target: { value: 'first-run password' } });
  fireEvent.click(screen.getByRole('button', { name: 'Create account' }));
  await screen.findByRole('button', { name: 'Sign in' });
  expect(send).toHaveBeenLastCalledWith(expect.objectContaining({
    method: 'POST', path: '/api/v1/auth/setup',
    body: { username: 'owner', password: 'first-run password', displayName: null },
  }));
  expect(reload).not.toHaveBeenCalled();
});

it('lets the reader check again after CLI setup', async () => {
  const send = vi.fn<ApiTransportPort['send']>()
    .mockResolvedValueOnce({ status: 200, statusText: 'OK', body: {
      mode: 'setup_required', needsOwnerSetup: true, canSetupInBrowser: false,
      setupBlockedReason: 'existing_data', nextStep: 'Run perga auth init-owner.',
    } })
    .mockResolvedValueOnce({ status: 200, statusText: 'OK', body: {
      mode: 'configured', needsOwnerSetup: false, canSetupInBrowser: false, setupBlockedReason: null, nextStep: null,
    } });
  render(<AuthGate transport={{ send }} reload={vi.fn()} />);
  expect(await screen.findByText('Run perga auth init-owner.')).toBeTruthy();
  expect(screen.queryByLabelText('Password')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Check again' }));
  expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy();
});

it('exchanges a pasted token and reloads only after the cookie response', async () => {
  const send = vi.fn<ApiTransportPort['send']>().mockImplementation((request: ApiRequest) => Promise.resolve(
    request.method === 'GET' ? { status: 200, statusText: 'OK', body: {
      mode: 'local_token_required', needsOwnerSetup: true, canSetupInBrowser: false, setupBlockedReason: null, nextStep: null,
    } } : { status: 200, statusText: 'OK', headers: { 'x-perga-session-id': 'loc_test' }, body: {
      mode: 'local', sessionId: 'loc_test', userId: 'local-owner', username: 'local', displayName: 'Local',
      role: 'owner', expiresAtMs: 2_000_000_000_000,
    } },
  ));
  const reload = vi.fn();
  render(<AuthGate transport={{ send }} reload={reload} />);
  fireEvent.change(await screen.findByLabelText('Local launch token'), { target: { value: ' pasted-token ' } });
  fireEvent.click(screen.getByRole('button', { name: 'Open workspace' }));
  await waitFor(() => expect(reload).toHaveBeenCalledOnce());
  expect(send).toHaveBeenLastCalledWith(expect.objectContaining({
    method: 'POST', path: '/api/v1/auth/local', body: { token: 'pasted-token' },
  }));
});

it('shows sign-in when a pasted token arrives after another client creates the owner', async () => {
  const send = vi.fn<ApiTransportPort['send']>()
    .mockResolvedValueOnce({ status: 200, statusText: 'OK', body: {
      mode: 'local_token_required', needsOwnerSetup: true, canSetupInBrowser: false, setupBlockedReason: null, nextStep: null,
    } })
    .mockResolvedValueOnce({ status: 409, statusText: 'Conflict', body: {
      code: 'owner_already_configured', error: 'The owner already exists.',
    } });
  const reload = vi.fn();
  render(<AuthGate transport={{ send }} reload={reload} />);
  fireEvent.change(await screen.findByLabelText('Local launch token'), { target: { value: 'stale-token' } });
  fireEvent.click(screen.getByRole('button', { name: 'Open workspace' }));

  expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy();
  expect(send).toHaveBeenCalledTimes(2);
  expect(send).toHaveBeenLastCalledWith(expect.objectContaining({
    method: 'POST', path: '/api/v1/auth/local', body: { token: 'stale-token' },
  }));
  expect(reload).not.toHaveBeenCalled();
});
