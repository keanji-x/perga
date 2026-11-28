import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { NewTrackForm } from './public.tsx';

afterEach(cleanup);

it.each(['/repo', null])('defaults to worktree isolation for folder %s', async (cwd) => {
  const onSubmit = vi.fn();
  render(<NewTrackForm area={{ id: 'work', name: 'Work' }} submitting={false} error={null}
    initialCwd={cwd}
    listDirectory={vi.fn()} onSubmit={onSubmit} />);
  expect(screen.queryByRole('button', { name: /Workspace isolation/ })).toBeNull();
  await userEvent.type(screen.getByLabelText('What this track should do'), 'Plan');
  await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
  expect(onSubmit).toHaveBeenLastCalledWith(cwd === null ? { message: 'Plan' }
    : { message: 'Plan', cwd, isolation: 'worktree' });
  if (cwd !== null) {
    await userEvent.click(screen.getByRole('button', { name: 'Repo: repo (/repo)' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: /Perga workspace/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    expect(onSubmit).toHaveBeenLastCalledWith({ message: 'Plan' });
  }
});

it('labels the creation preferences visibly without changing their selection semantics', () => {
  render(<NewTrackForm area={{ id: 'work', name: 'Work' }} submitting={false} error={null}
    initialCwd="/repo"
    listDirectory={vi.fn()} onSubmit={vi.fn()} />);
  expect(screen.queryByRole('button', { name: /^Template:/ })).toBeNull();
  expect(screen.getByRole('button', { name: 'Repo: repo (/repo)' }).textContent).toContain('Repo: repo');
});

it('switches between Worktree and Bare folder modes in the Repo popover and changes the folder in a second-level dialog', async () => {
  const onSubmit = vi.fn();
  const listDirectory = vi.fn().mockResolvedValue({ path: '/repo-2', parent: '/', entries: [] });
  render(<NewTrackForm area={{ id: 'work', name: 'Work' }} submitting={false} error={null}
    initialCwd="/repo"
    listDirectory={listDirectory} onSubmit={onSubmit} />);

  await userEvent.click(screen.getByRole('button', { name: 'Repo: repo (/repo)' }));
  expect(screen.queryByRole('dialog', { name: 'Choose a directory' })).toBeNull();

  await userEvent.click(await screen.findByRole('menuitem', { name: /Direct folder/ }));
  const updatedPill = screen.getByRole('button', { name: 'Repo: repo · Direct (/repo)' });
  expect(updatedPill.textContent).toContain('Repo: repo · Direct');
  expect(screen.queryByRole('dialog', { name: 'Choose a directory' })).toBeNull();

  // Clicking Change folder… opens the second-level DirectoryBrowser dialog.
  await userEvent.click(updatedPill);
  await userEvent.click(await screen.findByRole('menuitem', { name: /Change folder…/ }));
  const dialog = await screen.findByRole('dialog', { name: 'Choose a directory' });
  await within(dialog).findByDisplayValue('/repo-2/');
  await userEvent.click(within(dialog).getByRole('button', { name: 'Select this directory' }));

  const movedPill = screen.getByRole('button', { name: 'Repo: repo-2 · Direct (/repo-2)' });
  expect(movedPill.textContent).toContain('Repo: repo-2 · Direct');

  await userEvent.type(screen.getByLabelText('What this track should do'), 'Edit in source');
  await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
  expect(onSubmit).toHaveBeenLastCalledWith({ message: 'Edit in source', cwd: '/repo-2', isolation: 'none' });
});

it('shows only the managed workspace as selected until a folder is bound', async () => {
  render(<NewTrackForm area={{ id: 'work', name: 'Work' }} submitting={false} error={null}
    initialCwd={null}
    listDirectory={vi.fn()} onSubmit={vi.fn()} />);

  await userEvent.click(screen.getByRole('button', { name: 'Workspace: Managed' }));
  const menu = await screen.findByRole('menu');
  expect(within(menu).getAllByRole('menuitem')).toHaveLength(2);
  expect(within(menu).getByRole('menuitem', { name: /^Perga workspace.*Selected/ })).toBeTruthy();
  expect(within(menu).getByRole('menuitem', { name: /^Choose folder…/ })).toBeTruthy();
});
