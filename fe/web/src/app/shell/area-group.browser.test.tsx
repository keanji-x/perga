import { cleanup, render } from '@testing-library/react';
import { page, userEvent } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';
import '../../styles/entry.css';
import type { Area } from '../../../../core/domain/area.ts';
import { NEUTRAL_ACTIVITY, type Track } from '../../../../core/domain/track.ts';
import { SidebarFixture as Sidebar } from './sidebar-fixture.tsx';
import { readReceiptsFixture } from '../providers/read-receipts-fixture.tsx';

const receipts = readReceiptsFixture();

afterEach(() => { cleanup(); });

function element(node: HTMLElement | null) {
  expect(node).not.toBeNull();
  return node!;
}

function renderAreaTracks(trackCount = 7, loaded = true) {
  const area: Area = { id: 'work', name: 'Work', executionTargetId: 'jasper', color: '#5B8DEF', sort: 1, kind: 'user',
    defaultTemplateId: null, defaultCwd: null, createdAt: 1, updatedAt: 1 };
  const tracks: Track[] = Array.from({ length: trackCount }, (_, i) => ({
    id: `track-${i}`, areaId: area.id, title: `Row ${i + 1}`, sort: i, cwd: '/tmp', isolation: 'none', agentCwd: '/tmp',
    pinnedAt: null, closedAt: i === 7 ? 2 : null, createdAt: 1, updatedAt: 1, ...NEUTRAL_ACTIVITY,
  }));
  render(receipts.wrap(<div style={{ inlineSize: 240 }}><Sidebar areas={[area]} executionTargets={[{ id: 'jasper', label: 'Jasper workstation', kind: 'ssh', isDefault: false, removable: false }]} tracksByArea={loaded ? new Map([[area.id, tracks]]) : new Map()}
    tracks={tracks} currentPath="/" onGo={vi.fn()} onRequestCreateArea={vi.fn()} onRequestEditArea={vi.fn()}
    onDeleteArea={vi.fn()} onNewTrack={vi.fn()} onSetPinned={vi.fn()} onDeleteTrack={vi.fn()}
    onOpenSettings={vi.fn()} onOpenPlugins={vi.fn()} onSignOut={vi.fn()} collapsed={false} onToggleCollapsed={vi.fn()} /></div>));
}

/* `Show N more` is a row in the Track list, not a footer: same height, same text column. */
it('puts Show N more on the Track row rhythm, in the Track title column', async () => {
  await page.viewport(1400, 900);
  renderAreaTracks();
  const disclosure = page.getByRole('button', { name: 'Collapse area Work', exact: true });
  await expect.element(disclosure.getByText('Work · Jasper workstation', { exact: true })).toBeVisible();
  const more = element(document.querySelector<HTMLElement>('[aria-label="Show 2 more in Work · Jasper workstation"]'));
  const lastRow = element(document.querySelector<HTMLElement>('[aria-label^="Track Row 5"]'));
  const lastTitle = element(document.querySelector<HTMLElement>('[title="Row 5"]'));
  expect(more.getBoundingClientRect().height).toBeCloseTo(lastRow.getBoundingClientRect().height, 0);
  expect(more.getBoundingClientRect().top - lastRow.getBoundingClientRect().top).toBeCloseTo(30, 0);
  const range = document.createRange();
  range.selectNodeContents(more);
  expect(range.getBoundingClientRect().left).toBeCloseTo(lastTitle.getBoundingClientRect().left, 0);
});

it('previews the Track count before the row limit and follows Show closed', async () => {
  await page.viewport(1400, 900);
  renderAreaTracks(8);
  const area = page.getByRole('button', { name: 'Collapse area Work', exact: true });
  const preview = page.getByRole('tooltip');
  await expect.element(page.getByRole('button', { name: /^Show 2 more in Work/ })).toBeVisible();
  await area.hover();
  await expect.element(preview, { timeout: 2000 }).toBeVisible();
  await expect.element(preview.getByText('Work · Jasper workstation', { exact: true })).toBeVisible();
  await expect.element(preview.getByText('7 Tracks', { exact: true })).toBeVisible();
  expect(preview.element().getBoundingClientRect().left).toBeGreaterThanOrEqual(area.element().getBoundingClientRect().right);
  await page.screenshot({ path: './__screenshots__/area-track-count-preview.png' });

  await page.getByRole('button', { name: 'Area actions for Work' }).click();
  await page.getByRole('menuitem', { name: 'Show closed' }).click();
  await expect.element(page.getByRole('button', { name: /^Show 3 more in Work/ })).toBeVisible();
  await area.hover();
  await expect.element(preview.getByText('8 Tracks', { exact: true })).toBeVisible();

  await area.click();
  const expand = page.getByRole('button', { name: 'Expand area Work', exact: true });
  await expect.element(expand).toBeVisible();
  const newArea = page.getByRole('button', { name: 'New area' });
  await newArea.hover();
  (newArea.element() as HTMLButtonElement).focus();
  await userEvent.tab();
  await expect.element(expand).toHaveFocus();
  await expect.element(preview.getByText('8 Tracks', { exact: true })).toBeVisible();
  await userEvent.keyboard('{Escape}');
  await expect.element(preview).not.toBeInTheDocument();
  await expect.element(expand).toHaveFocus();
});

it('shows zero for a loaded empty Area and skips the preview for an unloaded one', async () => {
  await page.viewport(1400, 900);
  renderAreaTracks(0, false);
  const area = page.getByRole('button', { name: 'Collapse area Work', exact: true });
  await area.hover();
  await expect.element(area).not.toHaveAttribute('aria-describedby');
  cleanup();

  renderAreaTracks(0);
  await area.hover();
  await expect.element(page.getByRole('tooltip').getByText('0 Tracks', { exact: true })).toBeVisible();
});
