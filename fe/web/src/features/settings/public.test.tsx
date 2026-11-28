// @vitest-environment jsdom
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { bindSettingField, SettingFieldsList } from './fields.tsx';
import { SETTINGS_SECTIONS, settingsSection } from './navigation.tsx';
import { SettingsPane } from './pane.tsx';
import { AboutPane, AppearancePane } from './public.tsx';

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

describe('Settings pane metadata', () => {
  it.each(SETTINGS_SECTIONS)('uses the declared heading and optional lede for $id', ({ id, label }) => {
    const { lede } = settingsSection(id);
    render(<SettingsPane category={id}>Content</SettingsPane>);
    const pane = within(screen.getByRole('region', { name: label }));
    expect(pane.getByRole('heading', { name: label, level: 3 })).toBeTruthy();
    expect(pane.queryAllByRole('paragraph').map((node) => node.textContent)).toEqual(
      lede === undefined ? [] : [lede],
    );
  });
});

it('renders declared fields in order and sends each selection to its own binding', async () => {
  const onDensityChange = vi.fn<(value: 'compact' | 'cozy') => void>();
  const onSortChange = vi.fn<(value: 'recent' | 'title') => void>();
  render(<SettingFieldsList fields={[
    bindSettingField({
      id: 'density', label: 'Density', description: 'Controls row spacing.', control: 'segmented',
      options: [{ value: 'compact', label: 'Compact' }, { value: 'cozy', label: 'Cozy' }],
    }, { value: 'compact', onChange: onDensityChange }),
    bindSettingField({
      id: 'sort', label: 'Sort order', control: 'segmented',
      options: [{ value: 'recent', label: 'Recent' }, { value: 'title', label: 'Title' }],
    }, { value: 'recent', onChange: onSortChange }),
  ]} />);

  const [densityRow, sortRow] = screen.getAllByRole('listitem').map((row) => within(row));
  expect(densityRow.getByText('Controls row spacing.')).toBeTruthy();
  const density = within(densityRow.getByRole('radiogroup', { name: 'Density' }));
  const sort = within(sortRow.getByRole('radiogroup', { name: 'Sort order' }));
  expect(density.getByRole('radio', { name: 'Compact', checked: true })).toBeTruthy();
  expect(sort.getByRole('radio', { name: 'Recent', checked: true })).toBeTruthy();

  await userEvent.click(density.getByRole('radio', { name: 'Cozy' }));
  await userEvent.click(sort.getByRole('radio', { name: 'Title' }));
  expect(onDensityChange).toHaveBeenCalledExactlyOnceWith('cozy');
  expect(onSortChange).toHaveBeenCalledExactlyOnceWith('title');
});

describe('Settings appearance', () => {
  it('states the current theme in the radio group and reports the one the reader picks', async () => {
    const onThemeModeChange = vi.fn();
    render(<AppearancePane themeMode="system" onThemeModeChange={onThemeModeChange} />);
    const row = within(screen.getByRole('listitem'));
    expect(row.getByText("System follows your operating system's appearance.")).toBeTruthy();
    const control = within(row.getByRole('radiogroup', { name: 'Theme' }));
    expect(control.getByRole('radio', { name: 'System', checked: true })).toBeTruthy();

    await userEvent.click(control.getByRole('radio', { name: 'Dark' }));
    expect(onThemeModeChange).toHaveBeenCalledExactlyOnceWith('dark');
  });

  it('lets the reader select a theme with the arrow keys', async () => {
    const onThemeModeChange = vi.fn();
    render(<AppearancePane themeMode="light" onThemeModeChange={onThemeModeChange} />);
    expect(screen.getByRole('radio', { name: 'Light', checked: true })).toBeTruthy();
    await userEvent.tab();
    await userEvent.keyboard('{ArrowRight}');
    expect(onThemeModeChange).toHaveBeenCalledExactlyOnceWith('dark');
  });
});

it('copies the displayed build diagnostics and confirms the copy', async () => {
  const user = userEvent.setup();
  render(<AboutPane />);
  await user.click(screen.getByRole('button', { name: 'Copy diagnostics' }));
  expect(await screen.findByRole('button', { name: 'Copied' })).toBeTruthy();
  expect(await navigator.clipboard.readText()).toBe(`Perga\nVersion: ${__NC_VERSION__}\nBuild: ${__NC_BUILD__}`);
});
