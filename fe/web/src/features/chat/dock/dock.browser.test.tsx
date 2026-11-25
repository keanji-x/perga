import { cleanup, render } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, describe, expect, it, vi } from 'vitest';

import '../../../styles/entry.css';
import { ChatDock } from './public.tsx';

afterEach(() => { cleanup(); document.body.replaceChildren(); });

describe('ChatDock mobile geometry', () => {
  it.each([
    [390, 844],
    [320, 640],
  ] as const)('keeps the single-line input unclipped inside its capsule at %ix%i', async (width, height) => {
    await page.viewport(width, height);
    render(
      <div style={{ inlineSize: `calc(${width}px - 88px)` }}>
        <ChatDock text="" onChange={vi.fn()} onSend={vi.fn()} onBeginInput={vi.fn()} disabled={false} />
      </div>,
    );

    const form = document.querySelector<HTMLElement>('[data-nc-chat-dock]')!;
    const input = await page.getByRole('textbox', { name: 'Chat message' }).findElement() as HTMLTextAreaElement;
    const send = await page.getByRole('button', { name: 'Send chat message' }).findElement() as HTMLButtonElement;

    const formBox = form.getBoundingClientRect();
    const inputBox = input.getBoundingClientRect();
    const sendBox = send.getBoundingClientRect();
    const inputStyle = getComputedStyle(input);

    expect(formBox.height).toBe(48);
    expect(inputBox.top).toBeGreaterThanOrEqual(formBox.top);
    expect(inputBox.bottom).toBeLessThanOrEqual(formBox.bottom);
    expect(input.scrollHeight).toBeLessThanOrEqual(input.clientHeight);
    expect(inputStyle.whiteSpace).toBe('nowrap');
    expect(inputBox.right).toBeLessThanOrEqual(sendBox.left);
    expect(sendBox.right).toBeLessThanOrEqual(formBox.right);
  });

  it('keeps long draft text on one unclipped line at 320px', async () => {
    await page.viewport(320, 640);
    render(
      <div style={{ inlineSize: 'calc(320px - 88px)' }}>
        <ChatDock
          text="A very long single-line draft message that exceeds the 320px dock capsule width"
          onChange={vi.fn()}
          onSend={vi.fn()}
          onBeginInput={vi.fn()}
          disabled={false}
        />
      </div>,
    );
    const input = await page.getByRole('textbox', { name: 'Chat message' }).findElement() as HTMLTextAreaElement;
    expect(input.scrollHeight).toBeLessThanOrEqual(input.clientHeight);
    expect(getComputedStyle(input).whiteSpace).toBe('nowrap');
  });
});
