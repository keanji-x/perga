import { cleanup,render,screen,within } from '@testing-library/react';
import { afterEach,describe,expect,it,vi } from 'vitest';

import type { CardActivity } from '../../../../../core/domain/activity.ts';
import type { PanelItem } from '../../../../../core/domain/panel-item.ts';
import { createCardHost } from '../host.ts';
import { createCardRegistry } from '../registry.ts';
import { BoardHost } from '../ui/board-host.tsx';
import { partitionTrackCards } from './headless-filter.ts';
import { registerAvailableBuiltinCards } from './register.ts';

// The real surface never connects under jsdom (no socket or binary output), so this file replaces
// it with one that reports `connected` on mount.
vi.mock('../../terminal/surface.tsx', async () => {
  const { createElement, useEffect } = await import('react');
  return {
    TerminalSurface: ({ onStatusChange }: { onStatusChange?: (status: 'connected') => void }) => {
      useEffect(() => { onStatusChange?.('connected'); }, [onStatusChange]);
      return createElement('div', { 'data-testid': 'connected-surface' });
    },
  };
});

afterEach(cleanup);

/** The card head (`CardHead`'s root, marked as the drag handle) inside the mounted cell. */
const head = (): HTMLElement => {
  const found = document.querySelector<HTMLElement>('[data-nc-card-cell] [data-nc-card-drag]');
  if (found === null) throw new Error('no card head');
  return found;
};

function mountConnected(kind: string, activity: CardActivity | null) {
  const wire: PanelItem = { panelType: 'custom' as const,
    id: 'card-1', trackId: 'track-1', kind, title: null, sort: 1,
    payload: {}, removable: true, createdAtMs: 1, updatedAtMs: 2,
    terminal: { id: 'terminal-test', trackId: 'w1', title: '', cwd: '/tmp', createdAtMs: 1, status: 'running' },
  };
  const registry = createCardRegistry();
  registerAvailableBuiltinCards(registry);
  const card = partitionTrackCards(registry, [wire]).visible[0]?.card;
  if (card === undefined) throw new Error('Missing built-in card');
  return render(<BoardHost host={createCardHost(registry)} items={[
    { card, title: kind, originalIndex: 0, deletable: true, activity, notice: null },
  ]} visible activeCardId="card-1" />);
}

describe.each(['terminal'])('%s terminal head, connected', (kind) => {
  it('speaks the kernel verdict beside its marker', async () => {
    mountConnected(kind, 'working');
    await screen.findByTestId('connected-surface');
    expect(screen.queryByText('Connecting…')).toBeNull();
    expect(within(head()).getByText('Working')).toBeTruthy();
    const marker = head().querySelector('[data-nc-activity="working"]');
    expect(marker).not.toBeNull();
    expect(marker?.nextElementSibling).toBe(within(head()).getByText('Working'));
    expect(head().querySelectorAll('[data-nc-activity]')).toHaveLength(1);
  });

  it('speaks a request for input', async () => {
    mountConnected(kind, 'input');
    await screen.findByTestId('connected-surface');
    expect(within(head()).getByText('Needs input')).toBeTruthy();
    expect(head().querySelector('[data-nc-activity="attention"]')).not.toBeNull();
  });

  it('says nothing at all with no verdict', async () => {
    mountConnected(kind, null);
    await screen.findByTestId('connected-surface');
    expect(screen.queryByText('Connecting…')).toBeNull();
    expect(head().querySelector('[data-nc-activity]')).toBeNull();
    expect(head().querySelector('[role="status"]')).toBeNull();
    expect(within(head()).queryByText(/^(Working|Needs input|Needs attention|Unread updates)$/)).toBeNull();
  });
});
