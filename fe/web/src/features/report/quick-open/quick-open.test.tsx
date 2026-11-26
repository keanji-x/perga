import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';

import { QuickOpen } from './public.tsx';

afterEach(cleanup);

it.each(['draft:notes.txt', 'draft\\notes.txt', './~/notes.txt'])('selects the literal filesystem path %s', async path => {
  const onOpen = vi.fn();
  const onClose = vi.fn();
  render(<QuickOpen initialQuery="notes" recent={[]} lookup={{
    search: () => Promise.resolve([path]),
    list: () => Promise.resolve([]),
  }} onOpen={onOpen} onClose={onClose} />);

  fireEvent.click(await screen.findByRole('option', { name: path }));
  expect(onClose).toHaveBeenCalledOnce();
  expect(onOpen).toHaveBeenCalledExactlyOnceWith({ path });
});
