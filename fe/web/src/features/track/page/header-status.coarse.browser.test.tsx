import { cleanup } from '@testing-library/react';
import { page as browserPage, userEvent } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';
import '../../../styles/entry.css';
import { renderPage, track } from './test-fixtures.tsx';
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

it('describes activity on the mobile report composer entry', async () => {
  await browserPage.viewport(390, 844);
  renderPage({ track: track({ attention: 'input' }), mobileChatComposer: <div>Composer</div>, onReply: vi.fn() });
  await expect.element(browserPage.getByRole('button', { name: 'Open conversation', exact: true }))
    .toHaveAccessibleDescription('Needs input');
});
