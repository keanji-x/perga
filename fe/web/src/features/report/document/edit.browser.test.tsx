import { cleanup, render, screen } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';

import '../../../styles/entry.css';
import { ReportDocument } from './public.tsx';

afterEach(cleanup);

it('reveals the prose edit control on hover and keyboard focus without moving text', async () => {
  await page.viewport(1000, 800);
  render(<><p>Outside report</p><ReportDocument report={{ summary: '', body: '', docRev: 1,
    blocks: [{ id: 'editable', kind: 'prose', payload: { markdown: 'Original prose' } }] }}
    empty={<p>Empty</p>} onSaveProseBlock={vi.fn().mockResolvedValue(undefined)} /></>);
  const edit = screen.getByRole('button', { name: 'Edit block editable' });
  const prose = screen.getByText('Original prose');
  const original = prose.getBoundingClientRect();
  expect(getComputedStyle(edit).opacity).toBe('0');
  expect(edit.getBoundingClientRect().top).toBeGreaterThanOrEqual(original.bottom);
  await page.getByText('Original prose').hover();
  expect(getComputedStyle(edit).opacity).toBe('1');
  expect(prose.getBoundingClientRect()).toEqual(original);
  await page.getByText('Outside report').hover();
  expect(getComputedStyle(edit).opacity).toBe('0');
  edit.focus();
  expect(document.activeElement).toBe(edit);
  expect(getComputedStyle(edit).opacity).toBe('1');
  expect(prose.getBoundingClientRect()).toEqual(original);
  await page.getByRole('button', { name: 'Edit block editable' }).click();
  expect(screen.getByRole('textbox', { name: 'Markdown for block editable' })).toBeTruthy();
});

it.each([390, 1000])('keeps Edit below wrapping prose at %ipx and gives its editor the reading width', async width => {
  await page.viewport(width, 800);
  render(<ReportDocument report={{ summary: '', body: '', docRev: 1,
    blocks: [{ id: 'long', kind: 'prose', payload: { markdown: 'A long line of prose that wraps across the reading column. '.repeat(12) } }] }}
    empty={<p>Empty</p>} onSaveProseBlock={vi.fn().mockResolvedValue(undefined)} />);
  const edit = screen.getByRole('button', { name: 'Edit block long' });
  const prose = screen.getByText(/A long line of prose/);
  expect(edit.getBoundingClientRect().top).toBeGreaterThanOrEqual(prose.getBoundingClientRect().bottom);
  edit.focus();
  await page.getByRole('button', { name: 'Edit block long' }).click();
  const editor = screen.getByRole('textbox', { name: 'Markdown for block long' });
  expect(editor.getBoundingClientRect().width).toBe(editor.parentElement!.getBoundingClientRect().width);
  expect(getComputedStyle(editor).resize).toBe('vertical');
});
