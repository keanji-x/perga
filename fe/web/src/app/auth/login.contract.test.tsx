// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { LOGIN_TEXT } from '../../../../core/domain/login.ts';
import { LoginPage } from '../../features/auth/login-page/public.tsx';
import { loginWithTransport } from './login.ts';

afterEach(cleanup);

describe('production login assembly', () => {
  it('CAP-LOGIN-002 maps a production 401 to credential copy without broadcasting', async () => {
    const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });
    const broadcast = vi.fn(); unauthorized.subscribe(broadcast);
    const transport = { send: vi.fn().mockResolvedValue({
      status: 401, statusText: 'Unauthorized', body: { code: 'unauthorized', error: 'unauthorized' },
    }) };
    render(<LoginPage login={(username, password, signal) => loginWithTransport(transport, username, password, signal)} reload={vi.fn()} />);
    await userEvent.type(screen.getByLabelText('Username'), 'owner');
    await userEvent.type(screen.getByLabelText('Password'), 'wrong');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect((await screen.findByRole('alert')).textContent).toBe('Wrong username or password.');
    expect(broadcast).not.toHaveBeenCalled();
  });

  it('a throttled sign-in shows the server reason', async () => {
    const reason = 'Too many failed sign-in attempts. Try again in 32 seconds.';
    const transport = { send: vi.fn().mockResolvedValue({
      status: 429, statusText: 'Too Many Requests', body: { code: 'login_throttled', error: reason },
    }) };
    render(<LoginPage login={(username, password, signal) => loginWithTransport(transport, username, password, signal)} reload={vi.fn()} />);
    await userEvent.type(screen.getByLabelText('Username'), 'owner');
    await userEvent.type(screen.getByLabelText('Password'), 'secret');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect((await screen.findByRole('alert')).textContent).toBe(reason);
  });

  it('shows the owner setup guidance returned by the Host', async () => {
    const transport = { send: vi.fn().mockResolvedValue({
      status: 403, statusText: 'Forbidden', body: {
        code: 'auth_not_configured', error: 'Perga has no owner account.',
        nextStep: 'Run perga-ctl auth init-owner on the Perga host.',
      },
    }) };
    render(<LoginPage login={(username, password, signal) => loginWithTransport(transport, username, password, signal)} reload={vi.fn()} />);
    await userEvent.type(screen.getByLabelText('Username'), 'owner');
    await userEvent.type(screen.getByLabelText('Password'), 'secret');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect((await screen.findByRole('alert')).textContent)
      .toBe('Perga has no owner account. Run perga-ctl auth init-owner on the Perga host.');
  });

  it('shows a Retry-After countdown when throttled with Retry-After header', async () => {
    const transport = { send: vi.fn().mockResolvedValue({
      status: 429, statusText: 'Too Many Requests',
      headers: { 'retry-after': '12' },
      body: { code: 'login_throttled', error: 'Too many login attempts.' },
    }) };
    render(<LoginPage login={(username, password, signal) => loginWithTransport(transport, username, password, signal)} reload={vi.fn()} />);
    await userEvent.type(screen.getByLabelText('Username'), 'owner');
    await userEvent.type(screen.getByLabelText('Password'), 'secret');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByText('Try again in 12 seconds.')).toBeTruthy();
  });

  it('rejects whitespace-only credentials locally without calling transport', async () => {
    const transport = { send: vi.fn() };
    render(<LoginPage login={(username, password, signal) => loginWithTransport(transport, username, password, signal)} reload={vi.fn()} />);
    await userEvent.type(screen.getByLabelText('Username'), '   ');
    await userEvent.type(screen.getByLabelText('Password'), '   ');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect((await screen.findByRole('alert')).textContent).toBe('Enter your username and password.');
    expect(transport.send).not.toHaveBeenCalled();
  });

  it.each([
    ['a lost answer', () => Promise.reject(new TypeError('Failed to fetch'))],
    ['a 5xx', () => Promise.resolve({ status: 502, statusText: 'Bad Gateway', body: null })],
    ['an unreadable answer', () => Promise.resolve({ status: 200, statusText: 'OK', body: { unexpected: true } })],
  ])('an unknown sign-in (%s) shows the fixed sentence, never transport text', async (_case, answer) => {
    const transport = { send: vi.fn(answer) };
    render(<LoginPage login={(username, password, signal) => loginWithTransport(transport, username, password, signal)} reload={vi.fn()} />);
    await userEvent.type(screen.getByLabelText('Username'), 'owner');
    await userEvent.type(screen.getByLabelText('Password'), 'secret');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect((await screen.findByRole('alert')).textContent).toBe(LOGIN_TEXT.unknown);
  });
});
