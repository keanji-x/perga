// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CardEntry } from '../registry.js';
import { createCardHost, createCardRegistry } from '../public.js';
import { CardHead } from './card-head.tsx';
import { ActivePanelHost } from './active-panel-host.tsx';

declare module '../registry.js' {
  interface CardDataMap {
    activePanelTerm: { type: 'active-panel-host-term'; id: string; title: string | null; terminalId: string | null };
  }
}

const entry: CardEntry = {
  type: 'active-panel-host-term',
  component: () => (
    <div className="term live">
      <CardHead title="Build log" />
      <div className="term-body">term body</div>
    </div>
  ),
  title: () => 'Terminal',
  accessibleName: () => 'Terminal',
  create: Object.freeze({ mode: 'kernel-minted-only' as const }),
};

afterEach(cleanup);

describe('ActivePanelHost lifecycle', () => {
  it('replays the current visible and focused state when replacing the host', () => {
    const registry = createCardRegistry();
    registry.register(entry);
    const hostA = createCardHost(registry);
    const hostB = createCardHost(registry);
    const card = { type: 'active-panel-host-term' as const, id: 'card-a', title: null, terminalId: 't1', cwd: null, gateCwd: null };
    const items = [Object.freeze({ card, title: 'Build log', originalIndex: 0, activity: null, notice: null })];
    const board = render(
      <ActivePanelHost host={hostA} items={items} activeCardId="card-a" visible />,
    );

    expect(hostA.resolve('card-a')?.lifecycle.getSnapshot()).toMatchObject({
      visible: true,
      focused: true,
    });

    board.rerender(
      <ActivePanelHost host={hostB} items={items} activeCardId="card-a" visible />,
    );

    expect(hostA.resolve('card-a')).toBeNull();
    expect(hostB.resolve('card-a')?.lifecycle.getSnapshot()).toMatchObject({
      visible: true,
      focused: true,
    });

    board.unmount();
    expect(hostB.resolve('card-a')).toBeNull();
  });

  it('replays hidden visibility when replacing the host', () => {
    const registry = createCardRegistry();
    registry.register(entry);
    const hostA = createCardHost(registry);
    const hostB = createCardHost(registry);
    const card = { type: 'active-panel-host-term' as const, id: 'card-a', title: null, terminalId: 't1', cwd: null, gateCwd: null };
    const items = [Object.freeze({ card, title: 'Build log', originalIndex: 0, activity: null, notice: null })];
    const board = render(
      <ActivePanelHost host={hostA} items={items} activeCardId="card-a" visible />,
    );

    board.rerender(<ActivePanelHost host={hostA} items={items} activeCardId="card-a" visible={false} />);
    expect(hostA.resolve('card-a')?.lifecycle.getSnapshot()).toMatchObject({
      visible: false,
      focused: false,
    });

    board.rerender(
      <ActivePanelHost host={hostB} items={items} activeCardId="card-a" visible={false} />,
    );

    expect(hostA.resolve('card-a')).toBeNull();
    expect(hostB.resolve('card-a')?.lifecycle.getSnapshot()).toMatchObject({
      visible: false,
      focused: false,
    });

    board.unmount();
    expect(hostB.resolve('card-a')).toBeNull();
  });
});

describe('ActivePanelHost card removal', () => {
  const removable: CardEntry = {
    ...entry,
    component: ({ onRemove }) => (
      <div className="term live">
        <CardHead
          title="Build log"
          onDelete={onRemove}
          deleteLabel="Delete card Build log"
        />
        <div className="term-body">term body</div>
      </div>
    ),
  };

  function renderRemovable(options: {
    onRemoveCard?: (cardId: string) => void;
    removable?: boolean;
  }) {
    const registry = createCardRegistry();
    registry.register(removable);
    const host = createCardHost(registry);
    const card = { type: 'active-panel-host-term' as const, id: 'card-a', title: null, terminalId: 't1', cwd: null, gateCwd: null };
    return render(
      <ActivePanelHost
        host={host}
        items={[Object.freeze({
          card, title: 'Build log', originalIndex: 0, deletable: options.removable, activity: null, notice: null,
        })]}
        activeCardId="card-a"
        visible
        onRemoveCard={options.onRemoveCard}
      />,
    );
  }

  it('hands the component a remove bound to its own card id', async () => {
    const onRemoveCard = vi.fn();
    renderRemovable({ onRemoveCard, removable: true });
    await userEvent.click(screen.getByRole('button', { name: 'Delete card Build log' }));
    expect(onRemoveCard).toHaveBeenCalledWith('card-a');
  });

  it('draws no delete when the board was given none', () => {
    renderRemovable({ removable: true });
    expect(screen.queryByRole('button', { name: 'Delete card Build log' })).toBeNull();
  });

  it('withholds the delete on a kernel-owned card', () => {
    renderRemovable({ onRemoveCard: vi.fn(), removable: false });
    expect(screen.queryByRole('button', { name: 'Delete card Build log' })).toBeNull();
  });

  /* A host may omit a local removal override. */
  it('treats an absent removable bit as removable', async () => {
    const onRemoveCard = vi.fn();
    renderRemovable({ onRemoveCard });
    await userEvent.click(screen.getByRole('button', { name: 'Delete card Build log' }));
    expect(onRemoveCard).toHaveBeenCalledWith('card-a');
  });

  it('puts a delete on the unknown-card fallback head', async () => {
    const onRemoveCard = vi.fn();
    const registry = createCardRegistry();
    const host = createCardHost(registry);
    const card = { type: 'active-panel-host-term' as const, id: 'card-z', title: null, terminalId: null, cwd: null, gateCwd: null };
    render(
      <ActivePanelHost
        host={host}
        items={[Object.freeze({ card, title: 'Mystery', originalIndex: 0, activity: null, notice: null })]}
        activeCardId="card-z"
        visible
        onRemoveCard={onRemoveCard}
      />,
    );
    await userEvent.click(screen.getByRole('button', { name: 'Delete card Mystery' }));
    expect(onRemoveCard).toHaveBeenCalledWith('card-z');
  });
});

it('lazily mounts selections and retains DOM/controller identity through switching and hiding', () => {
  const dispose = vi.fn();
  const registry = createCardRegistry();
  registry.register({ ...entry, createController: () => ({ dispose }), component: ({ card }) => <input aria-label={card.id} /> });
  const host = { ...createCardHost(registry) };
  const mount = vi.spyOn(host, 'mount');
  const items = ['a', 'b', 'unvisited'].map((id, originalIndex) => ({
    card: { type: 'active-panel-host-term' as const, id, title: id, terminalId: id },
    title: id, originalIndex, activity: null, notice: null,
  }));
  const scene = (active: string | null, visible: boolean, next = items) => <ActivePanelHost host={host} items={next} activeCardId={active} visible={visible} />;
  const view = render(scene(null, false));
  expect(mount).not.toHaveBeenCalled();
  view.rerender(scene('a', true));
  const first = screen.getByRole('textbox', { name: 'a' });
  view.rerender(scene('b', true));
  expect(first.isConnected).toBe(true);
  expect(first.closest('[data-nc-card-cell]')?.hasAttribute('hidden')).toBe(true);
  expect(host.resolve('a')?.lifecycle.getSnapshot()).toMatchObject({ visible: false, focused: false });
  expect(host.resolve('b')?.lifecycle.getSnapshot()).toMatchObject({ visible: true, focused: true });
  expect(host.resolve('unvisited')).toBeNull();
  view.rerender(scene(null, false));
  expect(screen.queryByRole('textbox')).toBeNull();
  expect(dispose).not.toHaveBeenCalled();
  view.rerender(scene('a', true));
  expect(screen.getByRole('textbox', { name: 'a' })).toBe(first);
  expect(mount).toHaveBeenCalledTimes(2);
  view.rerender(scene('a', true, items.filter(item => item.card.id !== 'b')));
  expect(dispose).toHaveBeenCalledTimes(1);
  view.unmount();
  expect(dispose).toHaveBeenCalledTimes(2);
});
