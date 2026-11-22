// @vitest-environment jsdom
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CardWire, ThemeRgb } from '../../../../core/domain/track.ts';
import { useNewTrackCard } from '../../features/track/new-card/controller.tsx';
import { sameCardDraft, type CardCreatePort, type CardDraft, type KeyedCardBody } from '../../features/track/new-card/create.ts';
import { createCardRegistry, registerAvailableBuiltinCards } from '../../systems/cards/public.ts';
import { useKeyedIntent } from '../providers/idempotency-key.ts';

const CARD: CardWire = Object.freeze({ id: 'terminal-1', track_id: 'track-1', kind: 'terminal', title: null,
  sort: 1, payload: null, deletable: true, created_at: 1, updated_at: 1 });

afterEach(cleanup);

function setup(createTerminal: CardCreatePort['createTerminal']) {
  const registry = createCardRegistry();
  registerAvailableBuiltinCards(registry);
  const onCreated = vi.fn();
  const newTerminalSession = vi.fn();
  let theme: ThemeRgb = { bg: [1, 2, 3], fg: [4, 5, 6] };
  function Harness() {
    const creation = useNewTrackCard({ registry, cardCreatePort: { createTerminal },
      cardIntent: useKeyedIntent<CardDraft, KeyedCardBody>(sameCardDraft),
      directory: () => Promise.resolve({ path: '/', parent: null, entries: [] }),
      readTheme: () => theme, onCreated, compactViewport: true, newTerminalSession });
    return <>{creation.menu}{creation.surfaces}</>;
  }
  const view = render(<Harness />);
  return { ...view, onCreated, newTerminalSession, changeTheme: () => { theme = { bg: [7, 8, 9], fg: [10, 11, 12] }; } };
}

async function pickTerminal() {
  await userEvent.click(screen.getByRole('button', { name: 'Tools' }));
  await userEvent.click(await screen.findByRole('menuitem', { name: 'terminal' }));
}

describe('compact card creation controller', () => {
  it('retries an unconfirmed create with the original key and theme and opens only its confirmed result', async () => {
    const create = vi.fn<CardCreatePort['createTerminal']>()
      .mockRejectedValueOnce(new Error('lost response')).mockResolvedValueOnce(CARD);
    const { onCreated, changeTheme, newTerminalSession } = setup(create);
    await pickTerminal();
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Creating the terminal card is unconfirmed.');
    changeTheme();
    await userEvent.click(within(alert).getByRole('button', { name: 'Try again' }));
    await waitFor(() => { expect(onCreated).toHaveBeenCalledExactlyOnceWith(CARD.id); });
    expect(create).toHaveBeenCalledTimes(2);
    expect(create.mock.calls[1]).toEqual(create.mock.calls[0]);
    expect(newTerminalSession).not.toHaveBeenCalled();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('suppresses the landing of a create whose owner unmounted', async () => {
    let resolve!: (card: CardWire) => void;
    const create = vi.fn<CardCreatePort['createTerminal']>(() => new Promise(done => { resolve = done; }));
    const { unmount, onCreated } = setup(create);
    await pickTerminal();
    await waitFor(() => { expect(create).toHaveBeenCalledOnce(); });
    unmount();
    await act(async () => { resolve(CARD); await Promise.resolve(); });
    expect(onCreated).not.toHaveBeenCalled();
  });
});
