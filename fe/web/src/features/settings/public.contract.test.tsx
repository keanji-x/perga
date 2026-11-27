// @vitest-environment jsdom
// Invariants for the Settings surface. Behavior lives in public.test.tsx.
import { cleanup,render,screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach,describe,expect,it,vi } from 'vitest';

import { AccountPane } from './account.tsx';
import { SettingsSurface } from './public.tsx';

afterEach(cleanup);

describe('INV-A11Y-061 navigation shape', () => {

  it('names every settings section on a button, and marks the current one', async () => {
    const onSelectSection = vi.fn();
    render(
      <SettingsSurface presentation="desktop" section="appearance" onSelectSection={onSelectSection}>
        <span>pane</span>
      </SettingsSurface>,
    );
    const nav = screen.getByRole('navigation', { name: 'Settings sections' });
    expect([...nav.querySelectorAll('button')].map((node) => node.textContent))
      .toEqual(['Appearance', 'Planners', 'Account', 'About']);
    expect(screen.getByRole('button', { name: 'Appearance' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('button', { name: 'Planners' }).getAttribute('aria-current')).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Planners' }));
    expect(onSelectSection.mock.calls).toEqual([['planners']]);
  });
});


it('presents setup for Local and password reset information for an Owner', () => {
  const user = { sessionId: 'test', userId: 'test', username: 'mira', displayName: 'Mira',
    role: 'owner', expiresAtMs: 2_000_000_000_000 };
  const setup = <button type="button">Enable password protection</button>;
  const view = render(<AccountPane user={{ ...user, mode: 'local' }} setup={setup} />);
  expect(screen.getByRole('button', { name: 'Enable password protection' })).toBeTruthy();
  expect(screen.getByText(/Your local data stays in this workspace/)).toBeTruthy();

  view.rerender(<AccountPane user={{ ...user, mode: 'owner' }} setup={setup} />);
  expect(screen.getByText('Password protection is active.')).toBeTruthy();
  expect(screen.getByText('mira', { exact: true })).toBeTruthy();
  expect(screen.getByText('Mira', { exact: true })).toBeTruthy();
  expect(screen.getByText('perga auth passwd')).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Enable password protection' })).toBeNull();
});
