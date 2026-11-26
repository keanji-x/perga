import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { QuickOpen } from './public.tsx';

afterEach(cleanup);

it('cancels the previous lookup and ignores a response after abort', async () => {
  type Response = { signal: AbortSignal; resolve(paths: string[]): void };
  const requests = new Map<string, Response>();
  const search = vi.fn((query: string, signal: AbortSignal) => new Promise<string[]>(resolve => {
    requests.set(query, { signal, resolve });
  }));
  const open = vi.fn();
  render(<QuickOpen initialQuery="first" lookup={{ search, list: vi.fn() }} recent={[]} onOpen={open} onClose={vi.fn()} />);
  const input = screen.getByRole('combobox', { name: 'File path or name' });
  await waitFor(() => expect(requests.has('first')).toBe(true));
  fireEvent.change(input, { target: { value: 'second:12-14' } });
  expect(requests.get('first')?.signal.aborted).toBe(true);
  await waitFor(() => expect(requests.has('second')).toBe(true));
  await act(async () => { requests.get('second')?.resolve(['second.ts', 'other/second.ts']); await Promise.resolve(); });
  const secondOption = screen.getByRole('option', { name: 'other/second.ts' });
  await act(async () => { requests.get('first')?.resolve(['obsolete.ts']); await Promise.resolve(); });
  expect(screen.queryByRole('option', { name: 'obsolete.ts' })).toBeNull();
  fireEvent.keyDown(input, { key: 'ArrowDown' });
  expect(input.getAttribute('aria-activedescendant')).toBe(secondOption.id);
  fireEvent.keyDown(input, { key: 'Enter', isComposing: true });
  expect(open).not.toHaveBeenCalled();
  fireEvent.keyDown(input, { key: 'Enter' });
  expect(open).toHaveBeenCalledExactlyOnceWith({ path: 'other/second.ts', line: 12, endLine: 14 });
});
