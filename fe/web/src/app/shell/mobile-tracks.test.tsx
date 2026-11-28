// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Area } from '../../../../core/domain/area.ts';
import { NEUTRAL_ACTIVITY, type Track } from '../../../../core/domain/track.ts';
import { MobileTracks } from './mobile/tracks.tsx';

afterEach(cleanup);

const area: Area = {
  id: 'c1', name: 'Product', color: '#5B8DEF', sort: 1, kind: 'user',
  defaultTemplateId: null, defaultCwd: null, createdAt: 0, updatedAt: 0,
};
const track: Track = {
  id: 'w1', areaId: 'c1', title: 'Responsive mobile UI', sort: 1, cwd: '/tmp', isolation: 'none', agentCwd: '/tmp',
  pinnedAt: null, closedAt: null, createdAt: 0, updatedAt: 0, ...NEUTRAL_ACTIVITY,
};

describe('MobileTracks', () => {
  it('has one Area header, a More edit action, and grouped actions above the selected Track', async () => {
    const onEditArea = vi.fn(); const onOpenTrack = vi.fn(); const onBack = vi.fn(); const onNewTrack = vi.fn();
    render(<MobileTracks view="tracks" areas={[area]} tracksByArea={new Map([['c1', [track]]])} areaId="c1" currentTrackId="w1"
      onBack={onBack} onNewTrack={onNewTrack} onOpenSettings={vi.fn()} onCreateArea={vi.fn()} onSelectArea={vi.fn()} onEditArea={onEditArea} onOpenTrack={onOpenTrack}
      isUnread={() => false} readError={null} readLoading={false} onRetryRead={vi.fn()} />);
    const header = screen.getByRole('heading', { name: 'Product' }).closest('header');

    expect(screen.getByRole('button', { name: 'Back to Workspace' })).toBeTruthy();
    expect(screen.queryByRole('radio')).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Areas' })).toBeNull();
    const more = screen.getByRole('button', { name: 'Area actions' });
    await userEvent.click(more);
    const edit = screen.getByRole('menuitem', { name: 'Edit area Product' });
    expect(more.closest('header')).toBe(header);
    expect(more.querySelector('svg')).not.toBeNull();
    expect(screen.queryByRole('group', { name: 'Workspace actions' })).toBeNull();
    expect(screen.getByRole('button', { name: 'New track' }).closest('header')).toBe(header);
    await userEvent.click(edit);
    expect(onEditArea).toHaveBeenCalledWith(area, expect.objectContaining({ kind: 'anchor' }));
    await userEvent.click(screen.getByRole('button', { name: 'New track' }));
    expect(onNewTrack).toHaveBeenCalledWith('c1');
    const selected = screen.getByRole('button', { name: /^Responsive mobile UI\b/ });
    expect(selected.getAttribute('aria-current')).toBe('page');
    expect(selected.closest('section')).toBe(screen.getByRole('region', { name: 'Open tracks' }));
    expect(screen.queryByRole('heading', { name: 'Open tracks' })).toBeNull();
    await userEvent.click(selected);
    expect(onOpenTrack).toHaveBeenCalledWith('w1');
    await userEvent.click(screen.getByRole('button', { name: 'Back to Workspace' }));
    expect(onBack).toHaveBeenCalledOnce();
  });

  it('never reveals a system Area and still offers onboarding when no user Area is selected', async () => {
    const onCreateArea = vi.fn(); const onNewTrack = vi.fn(); const onEditArea = vi.fn();
    const onBack = vi.fn(); const onOpenSettings = vi.fn();
    render(<MobileTracks view="tracks" areas={[{ ...area, kind: 'system' }]} tracksByArea={new Map([['c1', [track]]])}
      areaId="c1" currentTrackId={undefined} onBack={onBack} onNewTrack={onNewTrack} onOpenSettings={onOpenSettings} onCreateArea={onCreateArea} onSelectArea={vi.fn()}
      onEditArea={onEditArea} onOpenTrack={vi.fn()} isUnread={() => false} readError={null} readLoading={false} onRetryRead={vi.fn()} />);
    expect(screen.queryByRole('button', { name: 'Product' })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Responsive mobile UI\b/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'New area' })).toBeNull();
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'New track' }).disabled).toBe(true);
    expect(screen.queryByRole('button', { name: 'Area actions' })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'New track' }));
    expect(onNewTrack).not.toHaveBeenCalled();
    expect(onEditArea).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'Back to Workspace' }));
    expect(onBack).toHaveBeenCalledOnce();
    expect(onOpenSettings).not.toHaveBeenCalled();
  });

  it('offers the Area list, New area, and Library Today/Memory navigation without workspace filters', async () => {
    const onCreateArea = vi.fn();
    const onOpenToday = vi.fn();
    const onOpenMemory = vi.fn();
    render(<MobileTracks view="areas" areas={[]} tracksByArea={new Map()} areaId={undefined} currentTrackId={undefined}
      onBack={vi.fn()} onNewTrack={vi.fn()} onOpenSettings={vi.fn()} onCreateArea={onCreateArea} onSelectArea={vi.fn()} onEditArea={vi.fn()} onOpenTrack={vi.fn()}
      onOpenToday={onOpenToday} onOpenMemory={onOpenMemory}
      isUnread={() => false} readError={null} readLoading={false} onRetryRead={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'New area' }).closest('header')).toBeNull();
    expect(screen.queryByRole('group', { name: 'Workspace actions' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'New track' })).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Areas' })).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Pinned' })).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Unread' })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'New area' }));
    expect(onCreateArea).toHaveBeenCalledOnce();
    await userEvent.click(screen.getByRole('button', { name: 'Today' }));
    expect(onOpenToday).toHaveBeenCalledOnce();
    await userEvent.click(screen.getByRole('button', { name: 'Memory' }));
    expect(onOpenMemory).toHaveBeenCalledOnce();
  });

  it('preserves read failure recovery instead of displaying a false empty list', async () => {
    const onRetryRead = vi.fn();
    render(<MobileTracks view="tracks" areas={[area]} tracksByArea={new Map()} areaId="c1" currentTrackId={undefined}
      onBack={vi.fn()} onNewTrack={vi.fn()} onOpenSettings={vi.fn()} onCreateArea={vi.fn()} onSelectArea={vi.fn()} onEditArea={vi.fn()} onOpenTrack={vi.fn()}
      isUnread={() => false} readError="Tracks are unavailable" readLoading={false} onRetryRead={onRetryRead} />);
    expect(screen.getByRole('alert').textContent).toContain('Tracks are unavailable');
    expect(screen.queryByText('这个 Area 还没有 Track')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(onRetryRead).toHaveBeenCalledOnce();
  });

  it('announces overlay activity and unread receipts without status icons', () => {
    const row = (name: RegExp) => screen.getByRole('button', { name });
    const mount = (overrides: Partial<Track>, unread = false) => (
      <MobileTracks view="tracks" areas={[area]} tracksByArea={new Map([['c1', [{ ...track, ...overrides }]]])} areaId="c1" currentTrackId={undefined}
        onBack={vi.fn()} onNewTrack={vi.fn()} onOpenSettings={vi.fn()} onCreateArea={vi.fn()} onSelectArea={vi.fn()} onEditArea={vi.fn()} onOpenTrack={vi.fn()}
        isUnread={() => unread} readError={null} readLoading={false} onRetryRead={vi.fn()} />
    );

    const view = render(mount({ working: false }));
    expect(row(/^Responsive mobile UI No updates yet$/).querySelector('svg')).toBeNull();

    view.rerender(mount({ closedAt: 5, working: true }));
    expect(row(/^Responsive mobile UI.* Running .* Closed$/).querySelector('svg')).toBeNull();

    view.rerender(mount({ attention: 'input' }));
    expect(row(/^Responsive mobile UI Waiting on you$/).querySelector('svg')).toBeNull();

    view.rerender(mount({ attention: 'failed' }));
    expect(row(/^Responsive mobile UI Needs repair$/).querySelector('svg')).toBeNull();

    view.rerender(mount({}, true));
    expect(row(/^Responsive mobile UI Unread update$/).querySelector('svg')).toBeNull();

    view.rerender(mount({ working: true }, true));
    expect(row(/^Responsive mobile UI Running Unread$/).querySelector('svg')).toBeNull();

    view.rerender(mount({ pinnedAt: 5 }, true));
    expect(row(/^Responsive mobile UI Unread update$/).querySelector('svg')).toBeNull();

    view.rerender(mount({ pinnedAt: 5 }));
    expect(row(/^Responsive mobile UI Pinned$/).querySelector('svg')).toBeNull();

    view.rerender(mount({ pinnedAt: 5, previewKind: 'summary', previewText: 'Release notes' }));
    expect(row(/^Responsive mobile UI Pinned · Summary:/).textContent).toContain('Pinned · Summary: Release notes');

    view.rerender(mount({ pinnedAt: 5, previewKind: 'reply', previewText: 'Ready for review' }));
    expect(row(/^Responsive mobile UI Pinned · Reply:/).textContent).toContain('Pinned · Reply: Ready for review');

    view.rerender(mount({ closedAt: 5 }, true));
    expect(row(/^Responsive mobile UI.* Closed .* Unread$/).querySelector('svg')).toBeNull();
  });
});

it('omits owner sharing from mobile navigation menus', async () => {
  render(<MobileTracks view="tracks" areas={[area]} tracksByArea={new Map([['c1', [track]]])} areaId="c1" currentTrackId="w1"
    onBack={vi.fn()} onNewTrack={vi.fn()} onOpenSettings={vi.fn()} onCreateArea={vi.fn()} onSelectArea={vi.fn()} onEditArea={vi.fn()} onOpenTrack={vi.fn()}
    trackActions={() => ({ areaPinned: false, onSetPinned: vi.fn(), onSetAreaPinned: vi.fn(), onMarkUnread: vi.fn() })}
    isUnread={() => false} readError={null} readLoading={false} onRetryRead={vi.fn()} />);
  await userEvent.click(screen.getByRole('button', { name: 'Area actions' }));
  expect(screen.queryByRole('menuitem', { name: 'Share area' })).toBeNull();
  await userEvent.keyboard('{Escape}');
  await userEvent.click(screen.getByRole('button', { name: 'Actions for track Responsive mobile UI' }));
  expect(screen.queryByRole('menuitem', { name: 'Share track' })).toBeNull();
});
