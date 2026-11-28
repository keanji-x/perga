import '../../../styles/entry.css';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { page } from 'vitest/browser';

import { NewTrackForm } from './public.tsx';

afterEach(cleanup);

it('places the Area in the desktop page header while keeping the composer masthead aligned', async () => {
  await page.viewport(1100, 800);
  const work = { id: 'work', name: 'Work' };
  const { container } = render(<NewTrackForm area={work} areas={[work, { id: 'other', name: 'Other' }]} onSelectArea={vi.fn()}
    submitting={false} error={null}
    initialCwd={null}
    onSubmit={vi.fn()}
    listDirectory={vi.fn(() => Promise.resolve({ path: '/', parent: null, entries: [] }))} />);
  const heading = screen.getByRole('heading', { name: 'What would you like to work on?' });
  const switcher = screen.getByRole('button', { name: 'Area: Work' });
  const header = switcher.closest('header');
  expect(header).not.toBeNull();
  expect(container.firstElementChild!.firstElementChild).toBe(header);
  expect(header!.matches('[data-nc-header-rows="1"]')).toBe(true);
  const headerBox = header!.getBoundingClientRect();
  expect(headerBox.bottom).toBeLessThan(heading.getBoundingClientRect().top);
  expect(switcher.getBoundingClientRect().right).toBeLessThan(heading.getBoundingClientRect().left);
  expect(Math.abs(switcher.querySelector('svg')!.getBoundingClientRect().left - header!.firstElementChild!.getBoundingClientRect().left)).toBeLessThanOrEqual(1);
  const mark = container.querySelector<SVGSVGElement>('[data-nc-brand-mark]')!.parentElement!;
  const composer = container.querySelector('[data-nc-new-track-message]')!;
  const left = mark.getBoundingClientRect().left;
  expect(Math.abs(composer.getBoundingClientRect().left - left)).toBeLessThan(1);
  const send = screen.getByRole('button', { name: 'Create track' });
  const chip = document.createElement('span');
  chip.style.backgroundColor = 'var(--surface-chip)';
  container.append(chip);
  const neutral = getComputedStyle(chip).backgroundColor;
  chip.remove();
  expect(getComputedStyle(send).backgroundColor).toBe(neutral);
  await page.getByRole('textbox', { name: 'What this track should do' }).fill('Plan the work');
  await expect.element(page.elementLocator(send)).toBeEnabled();
  expect(getComputedStyle(send).backgroundColor).not.toBe(neutral);
  for (const size of [22, 30]) {
    heading.style.fontSize = `${size}px`;
    expect(mark.getBoundingClientRect().height).toBeCloseTo(1.35 * size, 1);
    expect(mark.getBoundingClientRect().width).toBeCloseTo(1.35 * size, 1);
  }
  heading.style.removeProperty('font-size');
  heading.style.inlineSize = '180px';
  const iconBox = mark.getBoundingClientRect();
  const range = document.createRange();
  range.selectNode(heading.lastChild!);
  const textBoxes = range.getClientRects();
  expect(textBoxes.length).toBeGreaterThan(1);
  const textBox = textBoxes[0];
  expect(iconBox.top).toBeLessThan(textBox.bottom);
  expect(iconBox.bottom).toBeGreaterThan(textBox.top);
  heading.style.removeProperty('inline-size');
  await page.viewport(390, 800);
  expect(getComputedStyle(header!).display).toBe('none');
  expect(screen.queryByRole('button', { name: 'Area: Work' })).toBeNull();
  expect(mark.getBoundingClientRect().height).toBe(0);
  expect(Math.abs(composer.getBoundingClientRect().left - heading.getBoundingClientRect().left)).toBeLessThan(1);
});
