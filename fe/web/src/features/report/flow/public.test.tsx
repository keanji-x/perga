import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { FlowNodeProjection, FlowProjection } from '../../../../../core/domain/report-flow.ts';
import { balanceFlowRows, buildFlowLayout } from './layout.ts';
import { ReportFlowBlock } from './public.tsx';

afterEach(() => { cleanup(); });

function makeNode(overrides: Partial<FlowNodeProjection> & Pick<FlowNodeProjection, 'id' | 'title'>): FlowNodeProjection {
  return {
    source: 'agent',
    whenKind: 'immediate',
    after: [],
    doKind: 'run',
    canceled: false,
    status: 'pending',
    outcome: 'none',
    runCount: 0,
    firedCount: 0,
    waitingOn: [],
    gates: [],
    ...overrides,
  };
}

describe('buildFlowLayout', () => {
  it('layers branching DAG topologically and handles cycles defensively', () => {
    const nodes: FlowNodeProjection[] = [
      makeNode({ id: 'fix-r2', title: '修复 R1', status: 'done', outcome: 'ok' }),
      makeNode({ id: 'wait-ci', title: '等待 CI', whenKind: 'after', after: [{ node: 'fix-r2', on: 'ok' }], doKind: 'watch', status: 'waiting' }),
      makeNode({ id: 'recheck-a', title: '复审 A', whenKind: 'after', after: [{ node: 'wait-ci', on: 'ok' }] }),
      makeNode({ id: 'fix-ci', title: 'CI 红灯修复', whenKind: 'after', after: [{ node: 'wait-ci', on: 'attention' }] }),
      makeNode({ id: 'standalone-cron', title: '每日静默巡检', whenKind: 'cron', cron: '0 9 * * *' }),
    ];

    const layout = buildFlowLayout(nodes);
    expect(layout.hasCycle).toBe(false);
    expect(layout.layers).toHaveLength(3);
    expect(layout.layers[0].nodes.map((n) => n.node.id)).toEqual(['fix-r2']);
    expect(layout.layers[1].nodes.map((n) => n.node.id)).toEqual(['wait-ci']);
    expect(layout.layers[2].nodes.map((n) => n.node.id)).toEqual(['recheck-a', 'fix-ci']);
    expect(layout.cronNodes.map((n) => n.id)).toEqual(['standalone-cron']);

    const cyclic = buildFlowLayout([
      makeNode({ id: 'a', title: 'A', whenKind: 'after', after: [{ node: 'b', on: 'ok' }] }),
      makeNode({ id: 'b', title: 'B', whenKind: 'after', after: [{ node: 'a', on: 'ok' }] }),
    ]);
    expect(cyclic.hasCycle).toBe(true);
    expect(cyclic.layers).toHaveLength(1);
    expect(cyclic.layers[0].isCycleFallback).toBe(true);
    expect(cyclic.layers[0].nodes.map((n) => n.node.id)).toEqual(['a', 'b']);

    // Root feeding both sides of a cycle must still flag hasCycle and place cycle nodes in fallback.
    const rootedCycle = buildFlowLayout([
      makeNode({ id: 'root', title: 'Root' }),
      makeNode({ id: 'x', title: 'X', whenKind: 'after', after: [{ node: 'root', on: 'ok' }, { node: 'y', on: 'ok' }] }),
      makeNode({ id: 'y', title: 'Y', whenKind: 'after', after: [{ node: 'root', on: 'ok' }, { node: 'x', on: 'ok' }] }),
    ]);
    expect(rootedCycle.hasCycle).toBe(true);
    expect(rootedCycle.layers).toHaveLength(2);
    expect(rootedCycle.layers[0].nodes.map((n) => n.node.id)).toEqual(['root']);
    expect(rootedCycle.layers[1].isCycleFallback).toBe(true);
    expect(rootedCycle.layers[1].nodes.map((n) => n.node.id)).toEqual(['x', 'y']);
  });

  it('balances periodic rows without lonely single-item orphans', () => {
    const rowLengths = (count: number) =>
      balanceFlowRows(Array.from({ length: count }, (_, idx) => idx), 3).map((row) => row.length);

    expect(rowLengths(0)).toEqual([]);
    expect(rowLengths(1)).toEqual([1]);
    expect(rowLengths(2)).toEqual([2]);
    expect(rowLengths(3)).toEqual([3]);
    expect(rowLengths(4)).toEqual([2, 2]);
    expect(rowLengths(5)).toEqual([3, 2]);
    expect(rowLengths(6)).toEqual([3, 3]);
    expect(rowLengths(7)).toEqual([3, 2, 2]);
    expect(rowLengths(8)).toEqual([3, 3, 2]);
  });
});

describe('ReportFlowBlock (builtin:flow)', () => {
  it('renders layered branches, watch progress, attention auto-selection, worker jump, balanced periodic cards, and archived drawer without action buttons', () => {
    const onOpenWorker = vi.fn();

    const flow: FlowProjection = {
      rev: 5,
      filePath: '/data/tracks/trk_1/flow.json',
      nodes: [
        makeNode({
          id: 'fix-r2',
          title: '修复 R1 拦截项并推送',
          status: 'done',
          outcome: 'ok',
          outcomeSummary: '已修复边界条件并推送提交 `a1b2c3d`。',
          runCount: 1,
          model: 'claude-opus-4-6',
          sessionId: 'ses_impl_1',
          finishedAtMs: 1_760_000_000_000,
          gates: [
            { label: 'dotnet test', status: 'pass', command: 'dotnet test Perga.sln', output: 'All 42 tests passed.' },
          ],
        }),
        makeNode({
          id: 'wait-ci',
          title: '等待 PR #115 CI',
          whenKind: 'after',
          after: [{ node: 'fix-r2', on: 'ok' }],
          doKind: 'watch',
          status: 'waiting',
          watchProgress: {
            probeKind: 'prChecks',
            label: 'PR #115',
            passedChecks: 3,
            totalChecks: 5,
            lastCheckedAtMs: 1_760_000_060_000,
            timeoutAtMs: 1_760_001_800_000,
            detail: '2 checks still queued on ubuntu-latest',
          },
        }),
        makeNode({
          id: 'recheck-a',
          title: '复审 A（复用原上下文）',
          whenKind: 'after',
          after: [{ node: 'wait-ci', on: 'ok' }],
          targetSession: 'ses_reviewer_a',
          role: 'review',
          model: 'gpt-5.3-codex',
          status: 'pending',
          waitingOn: ['wait-ci'],
          prompt: '基于原上下文复审最新 diff。',
        }),
        makeNode({
          id: 'fix-ci',
          title: 'CI 红灯自动修复',
          whenKind: 'after',
          after: [{ node: 'wait-ci', on: 'attention' }],
          role: 'impl',
          status: 'done',
          outcome: 'attention',
          outcomeSummary: 'Unit test `FlowReconcilerTests` failed on Linux runner.',
          model: 'claude-sonnet-4-6',
          sessionId: 'ses_ci_fixer',
        }),
        makeNode({
          id: 'daily-ci-metrics',
          title: '每日 CI 指标采集',
          source: 'template',
          whenKind: 'cron',
          cron: '0 9 * * *',
          tz: 'Asia/Shanghai',
          cmd: './scripts/collect-ci.sh',
          runCount: 12,
          firedCount: 12,
          nextFireAtMs: 1_760_086_400_000,
        }),
      ],
      archived: [
        {
          id: 'design-r1',
          title: '架构初审',
          doKind: 'run',
          status: 'done',
          outcome: 'ok',
          outcomeSummary: 'L2 方案已定稿。',
          runCount: 1,
          sessionId: 'ses_arch_1',
          model: 'claude-opus-4-6',
          finishedAtMs: 1_759_900_000_000,
          archivedAtMs: 1_759_950_000_000,
        },
      ],
    };

    const { container } = render(
      <ReportFlowBlock
        flow={flow}
        onOpenWorker={onOpenWorker}
        renderText={(text) => <span>{text}</span>}
        renderMarkdown={(md) => <div data-testid="flow-md">{md}</div>}
      />,
    );

    // 1. Topological layers: fix-r2 (0) -> wait-ci (1) -> [recheck-a, fix-ci] (2)
    expect(container.querySelectorAll('[data-nc-flow-layer]')).toHaveLength(3);
    expect(screen.getByText('PR #115 · 3/5 checks · 等待中')).toBeTruthy();

    // 2. Attention node auto-selected by default with summary highlight (strictly read-only: no cancel button)
    const panel = screen.getByRole('article', { name: 'CI 红灯自动修复' });
    expect(within(panel).getByRole('alert').textContent).toContain('Unit test `FlowReconcilerTests` failed on Linux runner.');
    expect(within(panel).queryByRole('button', { name: '取消节点' })).toBeNull();

    // Clicking model in detail byline opens worker session
    fireEvent.click(within(panel).getByRole('button', { name: 'claude-sonnet-4-6' }));
    expect(onOpenWorker).toHaveBeenCalledWith('ses_ci_fixer');

    // 3. Clicking wait-ci shows watch progress details without any report mutation button
    fireEvent.click(screen.getByRole('button', { name: /^等待 PR #115 CI/ }));
    const watchPanel = screen.getByRole('article', { name: '等待 PR #115 CI' });
    expect(within(watchPanel).getByRole('region', { name: '探针等待进度' }).textContent).toContain('2 checks still queued');
    expect(within(watchPanel).queryByRole('button', { name: '取消节点' })).toBeNull();

    // 4. Clicking recheck-a shows `复用上下文` hint and clicking model jumps to targetSession
    fireEvent.click(screen.getByRole('button', { name: /^复审 A（复用原上下文）/ }));
    const recheckPanel = screen.getByRole('article', { name: '复审 A（复用原上下文）' });
    expect(recheckPanel.textContent).toContain('复用上下文');
    fireEvent.click(within(recheckPanel).getByRole('button', { name: 'gpt-5.3-codex' }));
    expect(onOpenWorker).toHaveBeenCalledWith('ses_reviewer_a');

    // 5. Clicking fix-r2 reveals Gate citation strip
    fireEvent.click(screen.getByRole('button', { name: /^修复 R1 拦截项并推送/ }));
    const fixPanel = screen.getByRole('article', { name: '修复 R1 拦截项并推送' });
    fireEvent.click(within(fixPanel).getByRole('button', { name: /dotnet test/ }));
    expect(within(fixPanel).getByRole('region', { name: 'dotnet test 输出' }).textContent).toContain('All 42 tests passed.');

    // 6. Periodic cron cards show schedule & actual run count
    const cronFooter = screen.getByRole('region', { name: '周期巡检规则' });
    expect(cronFooter.textContent).toContain('每日 CI 指标采集');
    expect(cronFooter.textContent).toContain('0 9 * * *');
    expect(cronFooter.textContent).toContain('已执行 12 次');

    // 7. Collapsible archived history drawer expands on click and links worker session
    const archiveToggle = screen.getByRole('button', { name: /已归档 1 项历史步骤/ });
    expect(archiveToggle.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('table', { name: '已归档历史步骤' })).toBeNull();

    fireEvent.click(archiveToggle);
    expect(archiveToggle.getAttribute('aria-expanded')).toBe('true');
    const archiveTable = screen.getByRole('table', { name: '已归档历史步骤' });
    expect(archiveTable.textContent).toContain('架构初审');
    fireEvent.click(within(archiveTable).getByRole('button', { name: 'claude-opus-4-6' }));
    expect(onOpenWorker).toHaveBeenCalledWith('ses_arch_1');
  });

  it('does not show waiting label on a settled watch node detail', () => {
    const flow: FlowProjection = {
      rev: 2,
      nodes: [
        makeNode({
          id: 'watch-done',
          title: '等待 PR #122 CI',
          doKind: 'watch',
          status: 'done',
          outcome: 'ok',
          outcomeSummary: 'PR #122 all 3 checks passed.',
          watchProgress: {
            probeKind: 'prChecks',
            label: 'PR #122',
            passedChecks: 3,
            totalChecks: 3,
            detail: 'PR #122 all 3 checks passed.',
          },
        }),
      ],
      archived: [],
    };
    render(<ReportFlowBlock flow={flow} />);
    const panel = screen.getByRole('article', { name: '等待 PR #122 CI' });
    const watchRegion = within(panel).getByRole('region', { name: '探针等待进度' });
    expect(watchRegion.textContent).toContain('PR #122 · 3/3 checks');
    expect(watchRegion.textContent).not.toContain('等待中');
  });
});
