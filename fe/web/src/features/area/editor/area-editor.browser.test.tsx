import { cleanup, render, screen } from '@testing-library/react';
import { page, userEvent } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';

import '../../../styles/entry.css';
import { Dialog } from '../../../ui/dialog/public.tsx';
import { AreaEditorForm } from './public.tsx';

afterEach(cleanup);

it('opens the directory picker from the Default menu in the footer', async () => {
  await page.viewport(800, 600);
  render(
    <Dialog purpose="form" origin={{ kind: 'surface-center' }} open title="New area" onClose={vi.fn()}>
      <AreaEditorForm
        initial={{ name: '', defaultCwd: null }}
        submitting={false}
        locked={false}
        cancelLabel="Cancel"
        error={null}
        listDirectory={() => Promise.resolve({ path: '/', parent: null, entries: [] })}
        nameInputRef={{ current: null }}
        submitLabel="Create"
        onCancel={vi.fn()}
        onSubmit={vi.fn()}
      />
    </Dialog>,
  );
  expect(screen.queryByRole('button', { name: /template|isolation/i })).toBeNull();
  const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'Name' });
  expect(input.placeholder).toBe('Area name');
  expect(screen.queryByText('Group related tracks under a shared workspace.')).toBeNull();
  const folder = screen.getByRole('button', { name: 'Default folder' });
  expect(folder.textContent).toBe('Default');
  expect(folder.getAttribute('data-size')).toBe('sm');
  expect(folder.getAttribute('data-variant')).toBe('secondary');
  const folderBox = folder.getBoundingClientRect();
  const cancelBox = screen.getByRole('button', { name: 'Cancel' }).getBoundingClientRect();
  const createBox = screen.getByRole('button', { name: 'Create' }).getBoundingClientRect();
  expect(folderBox.left).toBeCloseTo(input.form!.getBoundingClientRect().left);
  expect(folderBox.right).toBeLessThan(cancelBox.left);
  expect(createBox.right).toBeCloseTo(input.form!.getBoundingClientRect().right);
  const centers = [folderBox, cancelBox, createBox]
    .map((box) => box.top + box.height / 2);
  expect(Math.max(...centers) - Math.min(...centers)).toBeLessThan(1);
  await page.getByRole('button', { name: 'Default folder' }).click();
  const unboundItems = page.getByRole('menuitem').elements();
  expect(unboundItems.map((item) => item.textContent)).toEqual(['DefaultSelected', 'Choose folder…']);
  await page.getByRole('menuitem', { name: 'Choose folder…' }).click();
  await expect.element(page.getByRole('dialog', { name: 'Choose a directory' })).toBeVisible();
  expect(screen.queryByRole('menu')).toBeNull();
});

it('keeps the bound folder and form actions aligned at phone width', async () => {
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
        listDirectory={(path) => Promise.resolve({ path: path ?? '/', parent: null, entries: [] })}
        nameInputRef={{ current: null }}
        submitLabel="Save"
        onCancel={vi.fn()}
        onSubmit={vi.fn()}
      />
    </Dialog>,
  );

  const dialog = screen.getByRole('dialog', { name: 'Edit area' });
  const cancelBox = screen.getByRole('button', { name: 'Cancel' }).getBoundingClientRect();
  const saveBox = screen.getByRole('button', { name: 'Save' }).getBoundingClientRect();
  const folder = screen.getByRole('button', { name: 'Default folder: /srv/a-folder-with-a-very-long-name' });
  expect(folder.textContent).toBe('a-folder-with-a-very-long-name');
  const folderBox = folder.getBoundingClientRect();
  expect(folderBox.right).toBeLessThan(cancelBox.left);
  expect(Math.abs((folderBox.top + folderBox.height / 2) - (saveBox.top + saveBox.height / 2))).toBeLessThan(1);
  expect(dialog.scrollWidth).toBeLessThanOrEqual(dialog.clientWidth + 1);
  expect(Math.abs((cancelBox.top + cancelBox.height / 2) - (saveBox.top + saveBox.height / 2)))
    .toBeLessThan(1);
  await page.elementLocator(folder).click();
  expect(page.getByRole('menuitem').elements()[0]).toBe(page.getByRole('menuitem', { name: 'Default', exact: true }).element());
  await page.getByRole('menuitem', { name: /^Change folder….*\/srv\// }).click();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect.element(page.elementLocator(folder)).toHaveFocus();
  await userEvent.keyboard('{Enter}');
  await expect.element(page.getByRole('menuitem', { name: 'Default', exact: true })).toHaveFocus();
  await userEvent.keyboard('{Enter}');
  const defaultFolder = page.getByRole('button', { name: 'Default folder', exact: true });
  await expect.element(defaultFolder).toHaveFocus();
  expect(defaultFolder.element()).toBe(folder);
});
