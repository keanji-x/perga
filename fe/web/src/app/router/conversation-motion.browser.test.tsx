import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';
import { renderSendFeedbackFixture } from './conversation-send-feedback-fixture.tsx';

afterEach(async () => { cleanup(); await commands.emulateReducedMotion(false); });

it.each([false, true])('keeps the composer in place through v1 message acknowledgement (reduced motion: %s)', async reduced => {
  await page.viewport(1400, 900);
  await commands.emulateReducedMotion(reduced);
  const view = renderSendFeedbackFixture({ held: true });
  try {
    await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
    await expect.element(page.getByText('Retained reply.', { exact: true })).toBeVisible();
    const field = page.getByRole('combobox', { name: 'Message' });
    await field.fill('A stable message through acknowledgement.');
    const before = field.element().getBoundingClientRect();
    await userEvent.keyboard('{Enter}');
    const message = page.getByText('A stable message through acknowledgement.', { exact: true });
    await expect.element(message).toBeVisible();
    expect(Math.abs(field.element().getBoundingClientRect().top - before.top)).toBeLessThanOrEqual(1);
    view.release();
    await expect.element(page.getByRole('group', { name: 'Message composer', exact: true })).not.toHaveAttribute('aria-busy', 'true');
    await expect.element(field).toHaveAttribute('contenteditable', 'true');
    await expect.element(message).toBeVisible();
    expect(Math.abs(field.element().getBoundingClientRect().top - before.top)).toBeLessThanOrEqual(1);
    expect(page.getByText('A stable message through acknowledgement.', { exact: true }).elements()).toHaveLength(1);
  } finally { view.release(); }
});
