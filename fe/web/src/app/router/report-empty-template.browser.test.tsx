import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';

afterEach(cleanup);

it('renders the airy native-component blank guide with ProtocolBar, Flow, and Chronicle items without a template picker', async () => {
  await page.viewport(1440, 900);

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
      if (request.method === 'GET' && request.path === '/api/v1/tracks/project') {
        return {
          status: 200,
          statusText: 'OK',
          body: fixtureSnapshot(fixtureTrack('project', { areaId: 'project-area', title: '新项目' })),
        };
      }
      return undefined;
    },
  });

  await expect.element(page.getByRole('region', { name: 'Template protocol' })).toBeVisible();
  await expect.element(page.getByRole('region', { name: '起步流程' })).toBeVisible();
  await expect.element(page.getByRole('region', { name: '工作方式' })).toBeVisible();
  await expect.element(page.getByRole('heading', { name: '进展会整理在这里' })).toBeVisible();
  await expect.element(page.getByRole('heading', { name: '工作方式' })).toBeVisible();
  expect(document.querySelector('table')).toBeNull();
  expect(document.body.textContent).not.toContain('从模板起稿');

  await page.getByRole('button', { name: /^按需匹配模板/ }).click();
  await expect.element(page.getByText(/在动工前自动套用合适模板/)).toBeVisible();

  await page.getByRole('button', { name: /按需挂载的结构与编排/ }).click();
  await expect.element(page.getByText(/为本页铺好专属视图、数据与后续 Flow/)).toBeVisible();
});
