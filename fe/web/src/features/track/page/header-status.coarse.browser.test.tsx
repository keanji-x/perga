import { cleanup, render } from '@testing-library/react';
import { page as browserPage, userEvent } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';
import '../../../styles/entry.css';
import { TrackPage } from './public.tsx';
import { openableCardsOf, renderPage, track } from './test-fixtures.tsx';
afterEach(() => { cleanup(); document.body.replaceChildren(); });
it('shows blocked activity at the mobile chat entry without floating notifications', async () => {
  await browserPage.viewport(390, 844);
  const onReply = vi.fn();
  renderPage({ track: track({ attention: 'failed' }), onReply, onStartConversation: vi.fn() });
  const chat = browserPage.getByRole('button', { name: 'Chat', exact: true });
  await expect.element(chat).toBeVisible();
  await expect.element(chat).toHaveAccessibleDescription('Needs repair');
  expect(chat.element().querySelector('[data-nc-activity="failed"]')).not.toBeNull();
  expect(document.querySelector('[data-nc-notification-launcher]')).toBeNull();
  await userEvent.click(chat);
  expect(onReply).toHaveBeenCalledTimes(1);
});

it('omits the redundant mobile chat icon button beside the report composer dock while keeping track activity visible and preserving selector focus', async () => {
  await browserPage.viewport(390, 844);
  const titleHost = document.createElement('div');
  document.body.append(titleHost);
  const readView = (controls: Readonly<{ titleRef: (node: HTMLButtonElement | null) => void }>) => (
    <button type="button" ref={controls.titleRef}>Switch track</button>
  );
  const baseProps = {
    mobilePanelObscured: false,
    cards: [],
    existingCardIds: new Set<string>(),
    tasks: [],
    openableCards: openableCardsOf([], []),
    canReopenTrack: false,
    canCloseTrack: false,
    onRenameTrack: vi.fn(),
    closedControl: { pending: false, setClosed: () => Promise.resolve(true), feedback: { error: null, clear: () => undefined, run: () => Promise.resolve(true) } },
    onDeleteTrack: vi.fn(),
    onTrackDeleted: vi.fn(),
    panel: null,
    onOpenPanel: vi.fn(),
    onClosePanel: vi.fn(),
    mobileHeaderTitleHost: titleHost,
    mobileTitleReadView: readView,
    mobileChatComposer: <div>Composer</div>,
    onReply: vi.fn(),
  };

  const view = render(<TrackPage {...baseProps} track={track({ attention: 'none' })} />);
  expect(browserPage.getByRole('button', { name: 'Open conversation', exact: true }).query()).toBeNull();
  const switcher = titleHost.querySelector('button')!;
  switcher.focus();
  expect(document.activeElement).toBe(switcher);

  view.rerender(<TrackPage {...baseProps} track={track({ attention: 'input' })} />);
  expect(titleHost.querySelector('button')).toBe(switcher);
  expect(document.activeElement).toBe(switcher);
  const indicator = titleHost.querySelector<HTMLElement>('[data-nc-activity="attention"]');
  expect(indicator).not.toBeNull();
  expect(indicator!.getBoundingClientRect().height).toBeGreaterThan(0);
  expect(titleHost.textContent).toContain('Needs input');

  view.rerender(<TrackPage {...baseProps} track={track({ attention: 'none' })} />);
  expect(titleHost.querySelector('button')).toBe(switcher);
  expect(document.activeElement).toBe(switcher);
  expect(titleHost.querySelector('[data-nc-activity="attention"]')).toBeNull();
});
