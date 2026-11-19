import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';
import { renderSendFeedbackFixture } from './conversation-send-feedback-fixture.tsx';

afterEach(async () => { cleanup(); await commands.emulateReducedMotion(false); });

it.each([[false, false], [true, false], [false, true], [true, true]] as const)(
  'keeps a readable local message until the v1 receipt and item arrive (new session: %s, reduced motion: %s)', async (draft, reduced) => {
    await page.viewport(1400, 900);
    await commands.emulateReducedMotion(reduced);
    const view = renderSendFeedbackFixture({ held: true });
    try {
      await page.getByRole('button', { name: draft ? 'New conversation' : 'Conversation Daily Planner conversation', exact: true }).click();
      const field = page.getByRole('combobox', { name: 'Message', exact: true });
      await field.fill('A readable local message.');
      await userEvent.keyboard('{Enter}');
      await expect.element(page.getByRole('paragraph').filter({ hasText: /^A readable local message\.$/ })).toBeVisible();
      await expect.element(page.getByText('Sending…', { exact: true })).toBeVisible();
      view.release();
      await expect.element(page.getByText('Sending…', { exact: true })).not.toBeInTheDocument();
      await expect.element(page.getByRole('paragraph').filter({ hasText: /^A readable local message\.$/ })).toBeVisible();
      await expect.element(field).toHaveTextContent('');
      expect(page.getByRole('paragraph').filter({ hasText: /^A readable local message\.$/ }).elements()).toHaveLength(1);
      const writes = view.requests.filter(request => request.method === 'POST');
      expect(writes.map(request => request.path)).toEqual([draft ? '/api/v1/tracks/daily/sessions' : '/api/v1/sessions/daily-planner/messages']);
      expect(writes[0].headers?.['Idempotency-Key']).toBeTruthy();
      if (draft) expect(view.sessions.at(-1)?.id).toBe('server-chosen-session');
    } finally { view.release(); }
  });
