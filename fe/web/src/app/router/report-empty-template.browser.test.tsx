import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureReport, fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';

afterEach(cleanup);

it('applies a template directly from the blank report parchment ledger and replaces the empty state with the seeded report', async () => {
  await page.viewport(1440, 900);
  let applied = false;
  const appliedBodies: unknown[] = [];

  const templateManifest = {
    id: 'agents-delivery',
    title: 'AGENTS.md 阶段交付协议',
    description: '按需求、实现、评审与验证推进的交付流程。',
    extends: null,
    defaultPlugins: [],
    requireGateForReadWrite: false,
    afterWorkerDoneCmd: null,
    workspaceDriver: null,
    workspaceLowerCacheDirs: [],
    workspaceCreateCmd: null,
    workspaceCloseCmd: null,
    routing: [],
    initialReportMarkdown: '# 交付概览\n\n目标与验收标准已就绪。\n',
    inputsSchema: {},
    hasHeaderView: false,
    viewNames: null,
    flow: [],
  };

  renderDailyFixture({
    initial: '/track/project',
    reply: (request) => {
      if (request.method === 'GET' && request.path === '/api/v1/templates') {
        return { status: 200, statusText: 'OK', body: [templateManifest] };
      }
      if (request.method === 'POST' && request.path === '/api/v1/tracks/project/template') {
        applied = true;
        appliedBodies.push(request.body);
        return {
          status: 200,
          statusText: 'OK',
          body: fixtureTrack('project', {
            areaId: 'project-area',
            title: '新项目',
            templateId: 'agents-delivery',
            templateTitle: 'AGENTS.md 阶段交付协议',
          }),
        };
      }
      if (request.method === 'GET' && request.path === '/api/v1/tracks/project/template') {
        return {
          status: 200,
          statusText: 'OK',
          body: { snapshot: null, manifest: templateManifest, inputs: {}, data: {}, headerView: null },
        };
      }
      if (request.method === 'GET' && request.path === '/api/v1/tracks/project') {
        return {
          status: 200,
          statusText: 'OK',
          body: applied
            ? fixtureSnapshot(
                fixtureTrack('project', {
                  areaId: 'project-area',
                  title: '新项目',
                  templateId: 'agents-delivery',
                  templateTitle: 'AGENTS.md 阶段交付协议',
                }),
                [],
                fixtureReport('# 交付概览\n\n目标与验收标准已就绪。\n'),
              )
            : fixtureSnapshot(fixtureTrack('project', { areaId: 'project-area', title: '新项目' })),
        };
      }
      return undefined;
    },
  });

  await expect.element(page.getByRole('region', { name: 'Template protocol' })).toBeVisible();
  await expect.element(page.getByRole('region', { name: '起步流程' })).toBeVisible();
  await expect.element(page.getByRole('heading', { name: '进展会整理在这里' })).toBeVisible();
  await expect.element(page.getByRole('heading', { name: '从模板起稿' })).toBeVisible();
  const option = page.getByRole('button', { name: /AGENTS\.md 阶段交付协议/ });
  await expect.element(option).toBeVisible();
  await option.click();

  await expect.poll(() => appliedBodies.length).toBe(1);
  expect(appliedBodies[0]).toEqual({ templateId: 'agents-delivery' });
  await expect.element(page.getByRole('heading', { name: '交付概览' })).toBeVisible();
  await expect.poll(() => document.querySelector('[data-nc-report-empty]')).toBeNull();
});
