import { withFontRoleProbe } from '../../../../tools/styles/font-role-fixture.ts';
import { cleanup, render } from '@testing-library/react';
import { commands, page } from 'vitest/browser';
import { afterEach, describe, expect, it } from 'vitest';

import '../../styles/entry.css';

import { MobileHeader } from './public.tsx';

declare module 'vitest/browser' {
  interface BrowserCommands { setHorizontalSafeArea(left: number, right: number): Promise<void> }
}

afterEach(() => { cleanup(); document.body.replaceChildren(); });

const settlePaint = () => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));

describe('MobileHeader scroll surface', () => {
  it('clears asymmetric landscape safe areas and keeps the glyph and title centered vertically', async () => {
    await page.viewport(844, 390);
    render(<MobileHeader title="Report" backLabel="workspace" onBack={() => undefined}
      actions={<button type="button">More</button>} />);
    const back = page.getByRole('button', { name: 'Back to workspace' }).element();
    const more = page.getByRole('button', { name: 'More' }).element();
    const title = page.getByRole('heading', { name: 'Report' }).element();
    const initialLeft = back.getBoundingClientRect().left;
    const initialRight = more.getBoundingClientRect().right;
    const leftInset = 47;
    const rightInset = 23;

    await commands.setHorizontalSafeArea(leftInset, rightInset);
    try {
      expect(back.getBoundingClientRect().left - initialLeft).toBeCloseTo(leftInset, 0);
      expect(initialRight - more.getBoundingClientRect().right).toBeCloseTo(rightInset, 0);
      const glyphBox = back.querySelector('svg')!.getBoundingClientRect();
      const titleBox = title.getBoundingClientRect();
      expect(Math.abs((glyphBox.top + glyphBox.height / 2) - (titleBox.top + titleBox.height / 2))).toBeLessThanOrEqual(1);
    } finally {
      await commands.setHorizontalSafeArea(0, 0);
    }
  });

  it('keeps one flat title bar with the shared type scale while content scrolls beneath it', async () => {
    await page.viewport(390, 844);
    render(
      <div data-testid="scroll-host" style={{ blockSize: '200px', overflowY: 'auto', paddingInline: '16px' }}>
        <MobileHeader title="Report" backLabel="workspace" onBack={() => undefined} />
        <div style={{ blockSize: '800px' }}>Content</div>
      </div>,
    );

    const host = document.querySelector<HTMLElement>('[data-testid="scroll-host"]')!;
    const header = document.querySelector<HTMLElement>('[data-nc-mobile-header]')!;
    const top = header.getBoundingClientRect().top;
    expect(getComputedStyle(header).backdropFilter).toBe('none');

    host.scrollTop = 120;
    await settlePaint();

    expect(host.scrollTop).toBe(120);
    expect(getComputedStyle(header).backdropFilter).toBe('none');
    expect(getComputedStyle(header).borderRadius).toBe('0px');
    const title = await page.getByRole('heading', { name: 'Report', exact: true }).findElement();
    withFontRoleProbe('--type-page-title', header, expected => {
      expect(getComputedStyle(title)).toMatchObject(expected);
    });
    const tone = document.createElement('span');
    tone.style.color = 'var(--text)';
    host.appendChild(tone);
    expect(getComputedStyle(title).color).toBe(getComputedStyle(tone).color);
    const back = await page.getByRole('button', { name: 'Back to workspace' }).findElement();
    expect(getComputedStyle(back).borderRadius).toBe('12px');
    expect(back.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
    const backIcon = back.querySelector('svg')!;
    expect(backIcon.getBoundingClientRect().width).toBe(20);
    expect(backIcon.getBoundingClientRect().height).toBe(20);
    expect(Math.abs(header.getBoundingClientRect().top - top)).toBeLessThan(1);
  });
});
