import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ReportCheckpointsBlock } from './public.tsx';

describe('ReportCheckpointsBlock', () => {
  it('renders publication headline, byline author link, compact gate citations, and trigger/action/assign table', async () => {
    const user = userEvent.setup();
    const onOpenWorker = vi.fn();
    const renderMarkdown = vi.fn((md: string) => <div data-testid="rendered-md">{md}</div>);

    render(
      <ReportCheckpointsBlock
        payload={{
          items: [
            {
              id: 'design',
              title: '设计',
              status: 'done',
              statusLabel: '已对齐',
              summary: '核心目标与架构边界详见 [checkpoint-native-view.md](docs/design/checkpoint-native-view.md)。',
              docPath: 'docs/design/checkpoint-native-view.md',
              docLabel: '原生 Checkpoint 阶段交付视图设计',
              sessionId: 'ses_design',
              workerLabel: 'Worker #1',
              model: 'Claude Opus',
            },
            {
              id: 'impl-1',
              title: '实现一',
              status: 'done',
              docLabel: '内核 Checkpoint 契约与模型路由强校验',
              sessionId: 'ses_impl1',
              workerLabel: 'Worker #2',
              model: 'Claude Opus',
              checks: [
                {
                  label: 'Perga.Kernel.Tests',
                  status: 'pass',
                  detail: '92/92',
                  command: 'dotnet test tests/Perga.Kernel.Tests',
                  output: 'Passed! - Failed: 0, Passed: 92',
                },
              ],
            },
            {
              id: 'review-l2',
              title: 'L2 Review',
              status: 'pending',
              docLabel: '第二轮独立审查',
              triggers: [
                {
                  trigger: '实现二、L1 Review 完成',
                  action: '执行 L2 独立审查',
                  assignKind: 'model',
                  assign: 'GPT 6.2 SOAR',
                },
                {
                  trigger: 'L2 Review 通过',
                  action: '运行全量回归验证',
                  assignKind: 'command',
                  assign: 'dotnet test Perga.sln',
                },
              ],
            },
          ],
        }}
        renderMarkdown={renderMarkdown}
        onOpenWorker={onOpenWorker}
      />,
    );

    expect(screen.getByRole('heading', { name: '原生 Checkpoint 阶段交付视图设计' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: '阅读全文' })).toBeNull();
    expect(screen.getByTestId('rendered-md').textContent).toContain('docs/design/checkpoint-native-view.md');

    await user.click(screen.getByRole('button', { name: 'Claude Opus' }));
    expect(onOpenWorker).toHaveBeenCalledWith('ses_design');
    expect(screen.queryByText('Worker #1')).toBeNull();

    // Click compact Gate citation -> reveals footnote
    await user.click(screen.getByRole('tab', { name: /实现一/ }));
    expect(screen.queryByText(/Failed: 0, Passed: 92/)).toBeNull();
    await user.click(screen.getByRole('button', { name: /Perga\.Kernel\.Tests/ }));
    expect(screen.getByText(/Failed: 0, Passed: 92/)).toBeTruthy();

    // Switch to pending L2 Review -> shows structured Trigger / Action / Assign table
    await user.click(screen.getByRole('tab', { name: /L2 Review/ }));
    expect(screen.getByText('实现二、L1 Review 完成')).toBeTruthy();
    expect(screen.getByText('GPT 6.2 SOAR')).toBeTruthy();
    expect(screen.getByText('dotnet test Perga.sln')).toBeTruthy();
  });

  it('renders chronicle layout with inline worker assign, expect slots, standing trigger drawer, and progressive disclosure', async () => {
    const user = userEvent.setup();
    const onOpenWorker = vi.fn();

    render(
      <ReportCheckpointsBlock
        payload={{
          layout: 'chronicle',
          range: {
            unit: '$',
            current: { value: 137.5, display: '$137.50', label: '现价', note: '当前市场参考价' },
            points: [
              { value: 118, display: '$118', note: '云厂 CapEx 放缓至 <15%' },
              { value: 172, display: '$172', note: 'Blackwell/Rubin 按期迭代', emphasis: true },
              { value: 225, display: '$225', note: '推理需求指数级扩张' },
            ],
          },
          triggers: [
            { trigger: 'SEC 10-Q 发布', action: '生成财报拆解', assignKind: 'model', assign: 'Claude Opus' },
            { trigger: '每周一 08:30', action: '刷新指标快照', assignKind: 'command', assign: 'python snapshot.py' },
          ],
          items: [
            {
              id: 'i1',
              date: '2026-11-26',
              verdict: '待触发',
              triggerAt: 'SEC 8-K 发布',
              title: 'Q4 前瞻',
              status: 'running',
              model: 'Claude Opus',
              sessionId: 'ses_q4',
              summary: '跟踪云厂 CapEx。',
              expectations: [
                { label: 'Data Center 营收', expected: '≥ $46.0B', status: 'pending' },
              ],
            },
            {
              id: 'i2',
              date: '2026-10-15',
              verdict: '强化',
              title: 'GTC 2026',
              status: 'done',
              model: 'GPT 6.2 SOAR',
              sessionId: 'ses_gtc',
              summary: 'Rubin 发布。',
              expectations: [
                { label: '推理 TCO 降幅', expected: '≥ 5x', actual: '8.4x', status: 'beat' },
              ],
            },
            { id: 'i3', date: '2026-08-27', verdict: '已复盘', title: 'Q3 财报', status: 'done' },
            { id: 'i4', date: '2026-07-18', verdict: '可控', title: '出口管制细则', status: 'done' },
            { id: 'i5', date: '2026-05-28', verdict: '符合', title: 'Q2 财报', status: 'done' },
            { id: 'i6', date: '2026-02-26', verdict: '未达', title: 'Q1 财报', status: 'done' },
            { id: 'i7', date: '2025-11-20', verdict: '基线', title: 'FY25 Q4 财报', status: 'done' },
          ],
        }}
        onOpenWorker={onOpenWorker}
      />,
    );

    // Hovering a price point on the 1D range axis reveals its assumption without Bear/Base/Bull labels
    await user.hover(screen.getByRole('button', { name: /\$118/ }));
    expect(screen.getByText('云厂 CapEx 放缓至 <15%')).toBeTruthy();
    await user.hover(screen.getByRole('button', { name: /\$172/ }));
    expect(screen.getByText('Blackwell/Rubin 按期迭代')).toBeTruthy();

    // 7 items -> first 6 visible, 7th behind progressive disclosure
    expect(screen.queryByText('FY25 Q4 财报')).toBeNull();
    await user.click(screen.getByRole('button', { name: '展开更早 1 条记录' }));
    expect(screen.getByText('FY25 Q4 财报')).toBeTruthy();

    // Standing triggers toggle
    expect(screen.queryByText('SEC 10-Q 发布')).toBeNull();
    await user.click(screen.getByRole('button', { name: /自动监测/ }));
    expect(screen.getByText('SEC 10-Q 发布')).toBeTruthy();
    expect(screen.getByText('python snapshot.py')).toBeTruthy();

    // Expect slot renders pre-event pending expectation on default expanded item
    expect(screen.getByText('≥ $46.0B')).toBeTruthy();
    expect(screen.getByText('待兑现')).toBeTruthy();

    // Click model/assign right on a collapsed row -> opens worker chat directly
    await user.click(screen.getByRole('button', { name: 'GPT 6.2 SOAR' }));
    expect(onOpenWorker).toHaveBeenCalledWith('ses_gtc');

    // Expand GTC 2026 -> shows Expect vs Actual
    await user.click(screen.getByRole('button', { name: /GTC 2026/ }));
    expect(screen.getByText('8.4x')).toBeTruthy();
    expect(screen.getByText('超预期')).toBeTruthy();
  });

  it('renders clean 4-column OKR ledger (KR, current, target, updatedAt) and grouped roadmap loops', () => {
    render(
      <ReportCheckpointsBlock
        payload={{
          layout: 'chronicle',
          metrics: [
            {
              code: 'KR1',
              label: 'P95 执行时长',
              current: '9.4 min',
              targetLabel: '≤ 8.0 min',
              status: 'at_risk',
              updatedAt: '11-20 09:00',
            },
            {
              code: 'KR3',
              label: 'Flaky 误报率',
              tag: '11-19 新增',
              current: '1.1%',
              targetLabel: '≤ 0.5%',
              status: 'at_risk',
              updatedAt: '11-20 09:00',
            },
            {
              code: 'KR4',
              label: '核心仓库接入统一门禁',
              current: '4 / 6',
              targetLabel: '6 / 6',
              status: 'on_track',
              updatedAt: '11-18',
            },
            {
              code: 'KR6',
              label: '门禁契约冻结与文档化',
              current: '已达成',
              targetLabel: '零破窗变更',
              status: 'met',
              updatedAt: '11-14',
            },
          ],
          items: [
            { id: 'c1', group: '当前优化 Loop', date: 'KR1', verdict: '灰度中', title: '测试并行分片', status: 'running' },
            { id: 'c2', group: '下一轮 Roadmap', date: 'P1 · KR1', verdict: '条件待命', title: 'Rust Bridge 预编译镜像', status: 'pending' },
            { id: 'c3', group: '已闭环记录', date: '11-19', verdict: '已回滚', title: 'Playwright 8 并发实验', status: 'blocked' },
          ],
        }}
      />,
    );

    expect(screen.getByText('9.4 min')).toBeTruthy();
    expect(screen.getByText('11-19 新增')).toBeTruthy();
    expect(screen.getByText('4 / 6')).toBeTruthy();
    expect(screen.getByText('已达成')).toBeTruthy();
    expect(screen.getByText('11-18')).toBeTruthy();
    expect(screen.getByText('11-14')).toBeTruthy();
    expect(screen.getByRole('heading', { name: '当前优化 Loop' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: '下一轮 Roadmap' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: '已闭环记录' })).toBeTruthy();
  });
});
