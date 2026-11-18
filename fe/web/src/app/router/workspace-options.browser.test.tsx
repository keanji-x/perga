import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

afterEach(() => {
  cleanup();
  document.getElementById('root')?.remove();
});

it('renders Host workspace choices and requires a new choice when the selected mode disappears', async () => {
  await page.viewport(1280, 850);
  const fixture = renderDailyFixture({
    pergaMode: true,
    initial: '/area/project-area/new',
    reply: (request) => {
      const ok = (body: unknown) => ({ status: 200, statusText: 'OK', body });
      if (request.path === '/api/v1/areas')
        return ok([{ id: 'project-area', name: 'Project', color: '#123456', defaultCwd: '/repo', createdAtMs: 1, updatedAtMs: 1 }]);
      if (request.path === '/api/v1/templates') return ok([{ id: 'dev', title: 'Development', tasks: [], availability: { status: 'ready' } }]);
      const url = new URL(request.path, 'http://fixture.test');
      if (url.pathname === '/api/v1/areas/project-area/workspace-options') {
        const direct = { id: 'direct', label: 'Direct from Host', description: 'Use the source directory.' };
        return url.searchParams.get('cwd') === '/repo'
          ? ok({ defaultId: 'jj', options: [
            { id: 'jj', label: 'Jujutsu from Host', description: 'Create a JJ workspace.' },
            { id: 'git', label: 'Git from Host', description: 'Create a Git worktree.' },
            direct,
          ] })
          : ok({ defaultId: null, options: [direct] });
      }
      if (url.pathname === '/api/v1/fs/listdir') {
        const path = url.searchParams.get('path') ?? '/repo';
        return ok({ path, parent: '/', entries: path === '/repo' ? [{ name: 'other', is_dir: true }] : [] });
      }
      if (request.path === '/api/v1/tracks' && request.method === 'POST')
        return { status: 400, statusText: 'Bad Request', body: { code: 'invalid_argument', error: 'Captured request' } };
      return undefined;
    },
  });

  await expect.element(page.getByRole('combobox', { name: 'Template', exact: true })).not.toBeInTheDocument();
  const repo = page.getByRole('button', { name: /^Repo:/ });
  const create = page.getByRole('button', { name: 'Create track' });
  await page.getByRole('combobox', { name: 'What this track should do' }).fill('Keep the draft');
  await expect.element(create).toBeEnabled();
  await repo.click();
  await expect.element(page.getByRole('menuitem', { name: /Jujutsu from Host/ })).toBeVisible();
  await page.getByRole('menuitem', { name: /Git from Host/ }).click();

  await repo.click();
  await page.getByRole('menuitem', { name: /^Others…/ }).click();
  await page.getByRole('option', { name: 'other' }).click();
  await page.getByRole('button', { name: 'Select this directory' }).click();
  await expect.element(create).toBeDisabled();
  await repo.click();
  await expect.element(page.getByRole('menuitem', { name: /Git from Host/ })).not.toBeInTheDocument();
  await page.getByRole('menuitem', { name: /Direct from Host/ }).click();
  await create.click();

  const request = fixture.requests.find((item) => item.path === '/api/v1/tracks' && item.method === 'POST');
  expect(request?.body).toMatchObject({ cwd: '/repo/other', workspaceOptionId: 'direct', firstMessage: 'Keep the draft' });
  expect(request?.body).not.toHaveProperty('workspaceDriver');
  expect(request?.body).not.toHaveProperty('templateId');
  expect(fixture.requests.some(item => item.path === '/api/v1/templates')).toBe(false);
});
