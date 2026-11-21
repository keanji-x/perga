// @vitest-environment jsdom
import { cleanup,render,screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach,beforeEach,describe,expect,it,vi } from 'vitest';

import { AppearancePane } from './public.tsx';

beforeEach(() => {
  vi.stubGlobal('matchMedia', vi.fn(() => ({
    matches: false,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  })));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('Settings appearance', () => {
  it('states the current theme on a dropdown and reports the one the reader picks', async () => {
    const onThemeModeChange = vi.fn();
    render(<AppearancePane themeMode="system" onThemeModeChange={onThemeModeChange} />);
    const control = screen.getByRole('combobox', { name: 'Theme' });
    expect(control.textContent).toContain('System');

    await userEvent.click(control);
    await userEvent.click(await screen.findByRole('option', { name: 'Dark' }));
    expect(onThemeModeChange).toHaveBeenCalledWith('dark');
  });

  it('shows the active mode, not the first option', () => {
    render(<AppearancePane themeMode="light" onThemeModeChange={vi.fn()} />);
    expect(screen.getByRole('combobox', { name: 'Theme' }).textContent).toContain('Light');
  });
});
