import { useConversationEscape } from '../conversations/pane-lifecycle.ts';
import { cleanup, render, screen } from '@testing-library/react';
import { page, userEvent } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';
import '../../styles/entry.css';
import { Dialog } from '../../ui/dialog/public.tsx';
import { setup } from './mobile-browser-fixture.tsx';

afterEach(cleanup);

it('dismisses the Area Track menu on Escape while keeping the route active', async () => {
  await page.viewport(390, 844);
  const router = setup('/area/c1');
  await page.getByRole('button', { name: 'Actions for track Responsive mobile UI' }).first().click();
  await expect.element(page.getByRole('menuitem', { name: 'Unpin globally' })).toBeVisible();
  await userEvent.keyboard('{Escape}');
  await expect.element(page.getByRole('menuitem', { name: 'Unpin globally' })).not.toBeInTheDocument();
  await userEvent.keyboard('{Escape}');
  expect(router.state.location.pathname).toBe('/area/c1');
  await expect.element(page.getByRole('heading', { name: 'Product', exact: true })).toBeVisible();
});

it('leaves composing Escape to the IME before the conversation interrupt command', () => {
  const interrupt = vi.fn();
  function RunningConversation() {
    useConversationEscape({ open: {
      id: 'c1', provider: 'codex', trackId: 't1', trackTitle: 'Track', title: null,
      kind: 'codex', state: 'running', updatedAt: 0, turns: 1,
    }, store: { working: true, stopping: false, stopFeedback: null, interrupt } });
    return <div data-nc-conversation-region="conversation-c1"><input aria-label="Running conversation" /></div>;
  }
  render(<RunningConversation />);
  const field = screen.getByRole('textbox');
  for (const ime of [{ isComposing: true }, { keyCode: 229 }]) {
    const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true, ...ime });
    field.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
    expect(interrupt).not.toHaveBeenCalled();
  }
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
  expect(interrupt).toHaveBeenCalledOnce();
});

it('does not give the desktop rail an inactive mobile navigation depth', async () => {
  await page.viewport(1400, 900);
  setup('/track/w1');
  await page.getByRole('button', { name: 'Actions for track Responsive mobile UI' }).first().click();
  await expect.element(page.getByRole('menuitem', { name: 'Unpin globally' })).toBeVisible();
  const close = vi.fn();
  render(<Dialog purpose="info" open origin={{ kind: 'surface-center' }} title="Independent dialog" onClose={close}>Dialog content</Dialog>);
  await expect.element(page.getByRole('dialog', { name: 'Independent dialog' })
    .getByRole('button', { name: 'Close', exact: true })).toHaveFocus();
  await userEvent.keyboard('{Escape}');
  expect(close).toHaveBeenCalledOnce();
});
