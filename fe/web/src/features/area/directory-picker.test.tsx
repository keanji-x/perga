// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';

import type { ExecutionTargetDto } from '../../../../core/domain/area.ts';
import { Dialog } from '../../ui/dialog/public.tsx';
import { DirectoryPicker } from './directory-picker.tsx';

afterEach(cleanup);

it('updates machines in a pushed view without resetting the browsed path', async () => {
  const listDirectory = vi.fn((path?: string) => Promise.resolve({ path: path ?? '/work', parent: null, entries: [] }));
  const onCancel = vi.fn();
  const onSelect = vi.fn();
  function Host({ targets }: { targets?: readonly ExecutionTargetDto[] }) {
    return (
      <Dialog purpose="form" origin={{ kind: 'surface-center' }} open title="Area" onClose={onCancel}>
        <DirectoryPicker open initialPath="/work" targets={targets} targetEditable
          listDirectory={listDirectory} onCancel={onCancel} onSelect={onSelect} />
      </Dialog>
    );
  }
  const { rerender } = render(<Host />);
  const path = await screen.findByRole('combobox', { name: 'Directory path' });
  await userEvent.clear(path);
  await userEvent.type(path, '/work/child{Enter}');
  await waitFor(() => expect(listDirectory).toHaveBeenLastCalledWith('/work/child'));
  const calls = listDirectory.mock.calls.length;
  rerender(<Host targets={[
    { id: 'local', label: 'Local', kind: 'local', isDefault: true, destination: null, removable: false },
    { id: 'jasper', label: 'Jasper', kind: 'ssh', isDefault: false, destination: 'jasper@localhost', removable: true },
  ]} />);
  expect(await screen.findByRole('button', { name: 'Jasper' })).toBeTruthy();
  expect(screen.getByRole('combobox', { name: 'Directory path' })).toBe(path);
  expect((path as HTMLInputElement).value).toBe('/work/child/');
  expect(listDirectory).toHaveBeenCalledTimes(calls);
  await userEvent.click(screen.getByRole('button', { name: 'Select this directory' }));
  expect(onSelect).toHaveBeenCalledWith('/work/child', 'local');
});
