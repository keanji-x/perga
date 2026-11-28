import { cleanup, render, screen } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';

import '../../../styles/entry.css';
import { Dialog } from '../../../ui/dialog/public.tsx';
import { AreaEditorForm } from './public.tsx';

afterEach(cleanup);

it('uses a quiet caption and a full-width folder row above the creation actions', async () => {
  await page.viewport(800, 600);
  render(
    <Dialog purpose="form" origin={{ kind: 'surface-center' }} open title="New area" onClose={vi.fn()}>
      <AreaEditorForm
        initial={{ name: '', defaultCwd: null }}
        submitting={false}
        locked={false}
        cancelLabel="Cancel"
        error={null}
        listDirectory={vi.fn()}
        nameInputRef={{ current: null }}
        submitLabel="Create area"
        onCancel={vi.fn()}
        onSubmit={vi.fn()}
      />
    </Dialog>,
  );
  expect(screen.queryByRole('button', { name: /template|isolation/i })).toBeNull();
  const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'Name' });
  expect(input.placeholder).toBe('Area name');
  const subtitle = screen.getByText('Group related tracks under a shared workspace.');
  const title = screen.getByText('New area', { exact: true });
  expect(parseFloat(getComputedStyle(subtitle).fontSize)).toBeLessThan(parseFloat(getComputedStyle(title).fontSize));
  expect(subtitle.getBoundingClientRect().bottom).toBeLessThan(input.getBoundingClientRect().top);
  const folder = screen.getByRole('button', { name: 'Default folder: Perga workspace' });
  expect(getComputedStyle(folder).borderWidth).toBe('1px');
  expect(folder.textContent).toContain('Default folder · Managed by Perga');
  expect(folder.textContent).toContain('Choose');
  const folderBox = folder.getBoundingClientRect();
  const cancelBox = screen.getByRole('button', { name: 'Cancel' }).getBoundingClientRect();
  const createBox = screen.getByRole('button', { name: 'Create area' }).getBoundingClientRect();
  expect(folderBox.left).toBeCloseTo(input.form!.getBoundingClientRect().left);
  expect(folderBox.right).toBeCloseTo(createBox.right);
  expect(folderBox.bottom).toBeLessThan(cancelBox.top);
  const centers = [cancelBox, createBox]
    .map((box) => box.top + box.height / 2);
  expect(Math.max(...centers) - Math.min(...centers)).toBeLessThan(1);
});

it('wraps safely at phone width while keeping Cancel and Save together', async () => {
  await page.viewport(320, 640);
  render(
    <Dialog purpose="form" origin={{ kind: 'surface-center' }} open title="Edit area" onClose={vi.fn()}>
      <AreaEditorForm
        initial={{
          name: 'Work',

          defaultCwd: '/srv/a-folder-with-a-very-long-name',
        }}
        submitting={false}
        locked={false}
        cancelLabel="Cancel"
        error={null}
        listDirectory={vi.fn()}
        nameInputRef={{ current: null }}
        submitLabel="Save changes"
        onCancel={vi.fn()}
        onSubmit={vi.fn()}
      />
    </Dialog>,
  );

  const dialog = screen.getByRole('dialog', { name: 'Edit area' });
  const cancelBox = screen.getByRole('button', { name: 'Cancel' }).getBoundingClientRect();
  const saveBox = screen.getByRole('button', { name: 'Save changes' }).getBoundingClientRect();
  const clear = screen.getByRole('button', { name: 'Use a new Perga workspace' });
  expect(dialog.scrollWidth).toBeLessThanOrEqual(dialog.clientWidth + 1);
  expect(Math.abs((cancelBox.top + cancelBox.height / 2) - (saveBox.top + saveBox.height / 2)))
    .toBeLessThan(1);
  expect(clear.getBoundingClientRect().right).toBeLessThanOrEqual(dialog.getBoundingClientRect().right);
});
