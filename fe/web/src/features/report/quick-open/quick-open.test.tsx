import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';

import { createQuickOpenFileIndex, quickOpenFileEntries, quickOpenQuery } from '../../../../../core/domain/quick-open.ts';
import { useState } from '../../../ui/state/public.ts';
import { QuickOpen } from './public.tsx';

afterEach(cleanup);

function setup(initial: string, paths: string[]) {
  const onOpen = vi.fn();
  const onClose = vi.fn();
  const workspace = createQuickOpenFileIndex(paths);
  const recent = createQuickOpenFileIndex([]);
  function Example() {
    const [input, setInput] = useState(initial);
    const mode = quickOpenQuery(input);
    return <QuickOpen input={input} onInputChange={setInput} mode={mode}
      entries={quickOpenFileEntries(mode.kind === 'search' ? mode.query : '', recent, workspace)}
      busy={false} staleDirectory={false} searchPending={false} emptyMessage="No matching files"
      onOpen={onOpen} onClose={onClose} />;
  }
  render(<Example />);
  return { input: screen.getByRole('combobox'), onOpen, onClose };
}

it.each(['draft:notes.txt', 'draft\\notes.txt', './~/notes.txt'])('selects the literal filesystem path %s', path => {
  const { onOpen, onClose } = setup('notes', [path]);
  fireEvent.click(screen.getByRole('option', { name: path }));
  expect(onClose).toHaveBeenCalledOnce();
  expect(onOpen).toHaveBeenCalledExactlyOnceWith({ path });
});

it('highlights matching characters in both names and directories', () => {
  const { input } = setup('quick pub', ['quick-open/public.tsx']);
  const option = screen.getByRole('option', { name: 'quick-open/public.tsx' });
  expect(option.textContent).toBe('public.tsxquick-open');
  expect([...option.querySelectorAll('mark')].map(mark => mark.textContent)).toEqual(['pub', 'quick']);
  expect(input.getAttribute('aria-activedescendant')).toBe(option.id);
});

it('wraps selection and completes the active file while preserving the range spelling', () => {
  const { input, onOpen } = setup('note#L3-L5', ['a/note.ts', 'b/note.ts']);
  const last = screen.getByRole('option', { name: 'b/note.ts' });
  fireEvent.keyDown(input, { key: 'ArrowUp' });
  expect(input.getAttribute('aria-activedescendant')).toBe(last.id);
  fireEvent.keyDown(input, { key: 'Tab' });
  expect((input as HTMLInputElement).value).toBe('b/note.ts#L3-L5');
  fireEvent.keyDown(input, { key: 'Enter', isComposing: true });
  expect(onOpen).not.toHaveBeenCalled();
  fireEvent.keyDown(input, { key: 'Enter' });
  expect(onOpen).toHaveBeenCalledExactlyOnceWith({ path: 'b/note.ts', line: 3, endLine: 5 });
});
