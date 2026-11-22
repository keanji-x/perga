// @vitest-environment jsdom
// Invariants for the Settings surface. Behavior lives in public.test.tsx.
import { cleanup,render,screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach,describe,expect,it,vi } from 'vitest';

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
      .toEqual(['Appearance', 'Planners', 'About']);
    expect(screen.getByRole('button', { name: 'Appearance' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('button', { name: 'Planners' }).getAttribute('aria-current')).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Planners' }));
    expect(onSelectSection.mock.calls).toEqual([['planners']]);
  });
});
