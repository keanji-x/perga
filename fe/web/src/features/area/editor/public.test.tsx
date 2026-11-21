// @vitest-environment jsdom

import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { Dialog } from '../../../ui/dialog/public.tsx';
import { AreaEditorForm, type AreaEditorFormProps } from './public.tsx';

afterEach(cleanup);

function renderForm(overrides: Partial<AreaEditorFormProps> = {}) {
  const onSubmit = vi.fn();
  const props: AreaEditorFormProps = {
    initial: { name: 'Work', defaultCwd: '/srv/work' },
    submitting: false,
    locked: false,
    cancelLabel: 'Cancel',
    error: null,
    listDirectory: vi.fn((path?: string) =>
      Promise.resolve({ path: path ?? '/', parent: null, entries: [] })),
    nameInputRef: { current: null },
    submitLabel: 'Save changes',
    onCancel: vi.fn(),
    onSubmit,
    ...overrides,
  };
  return {
    onSubmit,
    ...render(
      <Dialog purpose="form" origin={{ kind: 'surface-center' }} open title="Edit Work" onClose={props.onCancel} initialFocusRef={props.nameInputRef}>
        <AreaEditorForm {...props} />
      </Dialog>,
    ),
  };
}

describe('AreaEditorForm', () => {
  it('creates without template inputs and preserves a chosen directory regardless of action copy', async () => {
    const onSubmit = vi.fn();
    render(
      <Dialog purpose="form" origin={{ kind: 'surface-center' }} open title="New area" onClose={vi.fn()}>
        <AreaEditorForm
          initial={{ name: 'Work', defaultCwd: null }}
          submitting={false} locked={false} error={null}
          cancelLabel="Cancel" submitLabel="Save changes"
          listDirectory={() => Promise.resolve({ path: '/srv/ legal-space ', parent: null, entries: [] })}
          nameInputRef={{ current: null }} onCancel={vi.fn()} onSubmit={onSubmit}
        />
      </Dialog>,
    );
    expect(screen.queryByRole('button', { name: /template|isolation/i })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Default folder: Neige workspace' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Select this directory' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(onSubmit).toHaveBeenCalledWith({
      name: 'Work', defaultCwd: '/srv/ legal-space ',
    });
  });

  it('pushes the directory browser into its host Dialog instead of nesting a modal', async () => {
    renderForm();
    const opener = screen.getByRole('button', { name: 'Default folder: /srv/work' });
    await userEvent.click(opener);
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
    expect(screen.getByRole('dialog', { name: 'Choose a directory' })).toBeTruthy();
    const path = await screen.findByRole('combobox', { name: 'Directory path' });
    await waitFor(() => expect(document.activeElement).toBe(path));
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.getByRole('dialog', { name: 'Edit Work' })).toBeTruthy();
    await waitFor(() => expect(document.activeElement).toBe(opener));
  });

  it('renders a save failure in the form', () => {
    renderForm({ error: 'Could not save the Area.' });
    expect(screen.getByRole('alert').textContent).toContain('Could not save the Area.');
  });

  it('preserves legal leading and trailing spaces in an attached path', async () => {
    const onSubmit = vi.fn();
    renderForm({
      initial: { name: 'Work', defaultCwd: '/srv/ work ' },
      onSubmit,
    });
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(onSubmit).toHaveBeenCalledWith({
      name: 'Work', defaultCwd: '/srv/ work ',
    });
  });

  it('allows choosing an execution target on creation, resets default folder on switch, and scopes directory browsing', async () => {
    const onSubmit = vi.fn();
    const listerCalls: string[] = [];
    const listDirectoryForTarget = (targetId: string) => {
      listerCalls.push(targetId);
      return vi.fn((path?: string) =>
        Promise.resolve({ path: path ?? `/home/${targetId}`, parent: null, entries: [] }));
    };

    renderForm({
      initial: { name: 'Jasper Area', defaultCwd: '/home/kenji/repo', executionTargetId: 'local' },
      targetEditable: true,
      executionTargets: [
        { id: 'local', label: 'Local (kenji)', kind: 'local', isDefault: true },
        { id: 'jasper', label: 'jasper (local ssh)', kind: 'ssh', isDefault: false },
      ],
      listDirectoryForTarget,
      submitLabel: 'Create area',
      onSubmit,
    });

    const select = screen.getByLabelText('Execution target');
    await userEvent.selectOptions(select, 'jasper');
    expect(listerCalls).toContain('jasper');

    // Switching target clears the previous target's default folder so paths cannot leak across targets.
    await userEvent.click(screen.getByRole('button', { name: 'Create area' }));
    expect(onSubmit).toHaveBeenCalledWith({
      name: 'Jasper Area',
      defaultCwd: null,
      executionTargetId: 'jasper',
    });
  });
});
