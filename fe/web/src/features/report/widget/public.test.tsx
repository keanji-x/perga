import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TemplateViewsProjection } from '../../../../../core/domain/report-widget.ts';
import type { TrackReport } from '../../../../../core/domain/report.ts';
import { ReportDocument } from '../document/public.tsx';
import { ReportWidgetBlock } from './public.tsx';

afterEach(() => { cleanup(); });

describe('ReportWidgetBlock (Scheme B Unified VO)', () => {
  it('renders builtin:stepper and builtin:table natively from projected views', () => {
    const projection: TemplateViewsProjection = {
      templateId: 'demo',
      views: {
        stepperView: {
          items: [
            { id: 'design', title: '设计', status: 'done', summary: '完成架构设计' },
            { id: 'impl', title: '实现', status: 'running', summary: '正在编码' },
          ],
        },
        krView: {
          metrics: [
            { code: 'KR1', label: 'P95 时长', current: '11.2 min', targetLabel: '≤ 8.0 min', status: 'at_risk', updatedAt: '11-21 09:00' },
          ],
        },
      },
      widgets: {},
      dataRev: 0,
      flowRev: 0,
      diagnostics: [],
      humanReadOnly: false,
    };

    const { rerender } = render(
      <ReportWidgetBlock
        payload={{ use: 'builtin:stepper', view: 'stepperView' }}
        projection={projection}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(screen.getByText('设计')).toBeTruthy();
    expect(screen.getByText('正在编码')).toBeTruthy();

    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:table', view: 'krView' }}
        projection={projection}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(screen.getByText('KR1')).toBeTruthy();
    expect(screen.getByText('11-21 09:00')).toBeTruthy();
  });

  it('mounts seamless HTML graphic widget with sandbox="allow-scripts" and handles resize + whitelisted intents', () => {
    const onOpenFile = vi.fn();
    const onOpenWorker = vi.fn();
    const projection: TemplateViewsProjection = {
      templateId: 'equity-research',
      views: {
        valuationAxis: { current: { value: 137.5, display: '$137.50' }, points: [{ value: 172, display: '$172', note: 'base' }] },
      },
      widgets: {
        './widgets/range-axis.html': '<!doctype html><html><body>axis</body></html>',
      },
      dataRev: 0,
      flowRev: 0,
      diagnostics: [],
      humanReadOnly: true,
    };

    render(
      <ReportWidgetBlock
        payload={{ use: './widgets/range-axis.html', view: 'valuationAxis', chrome: 'seamless' }}
        projection={projection}
        renderText={(text) => <span>{text}</span>}
        onOpenFile={onOpenFile}
        onOpenWorker={onOpenWorker}
      />,
    );

    const iframe = screen.getByTitle<HTMLIFrameElement>('valuationAxis');
    expect(iframe.getAttribute('sandbox')).toBe('allow-scripts');

    // Simulate resize + valid open_file + blocked traversal + valid open_session from iframe window
    fireEvent(window, new MessageEvent('message', {
      source: iframe.contentWindow,
      data: { type: 'perga:resize', height: 186 },
    }));
    expect(iframe.style.blockSize).toBe('186px');

    fireEvent(window, new MessageEvent('message', {
      source: iframe.contentWindow,
      data: { type: 'perga:intent', kind: 'open_file', path: 'nvda-baseline-thesis.md' },
    }));
    // All path traversal, backslash, scheme, query, fragment, and spoofed-source payloads must be dropped
    for (const badPath of ['../escape.md', '..\\escape.md', '/etc/passwd', 'https://evil.example/x', 'javascript:alert(1)', 'a.md?x=1', 'a.md#top']) {
      fireEvent(window, new MessageEvent('message', {
        source: iframe.contentWindow,
        data: { type: 'perga:intent', kind: 'open_file', path: badPath },
      }));
    }
    fireEvent(window, new MessageEvent('message', {
      source: window,
      data: { type: 'perga:intent', kind: 'open_file', path: 'spoofed-source.md' },
    }));
    fireEvent(window, new MessageEvent('message', {
      source: iframe.contentWindow,
      data: { type: 'perga:intent', kind: 'open_session', sessionId: 'ses_nvda_1' },
    }));

    expect(onOpenFile).toHaveBeenCalledTimes(1);
    expect(onOpenFile).toHaveBeenCalledWith('nvda-baseline-thesis.md');
    expect(onOpenWorker).toHaveBeenCalledWith('ses_nvda_1');
  });

  it('renders empty builtin:list gracefully and surfaces schema/loading/error states', () => {
    const emptyProjection: TemplateViewsProjection = {
      templateId: 'equity-research',
      views: {
        chronicle: { items: [] },
        emptyLedger: { metrics: [] },
        badLedger: { metrics: [{ label: 123 }] },
      },
      widgets: {},
      dataRev: 4,
      flowRev: 0,
      diagnostics: [{ view: 'brokenView', message: 'boom' }],
      humanReadOnly: true,
    };

    const { rerender } = render(
      <ReportWidgetBlock
        payload={{ use: 'builtin:list', view: 'chronicle', title: '事件跟踪' }}
        projection={{ status: 'ready', projection: emptyProjection }}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(screen.getByText('当前暂无记录。')).toBeTruthy();

    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:table', view: 'emptyLedger' }}
        projection={{ status: 'ready', projection: emptyProjection }}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(screen.getByText('当前暂无可展示条目。')).toBeTruthy();

    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:table', view: 'badLedger' }}
        projection={{ status: 'ready', projection: emptyProjection }}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(screen.getByRole('alert').textContent).toContain('视图契约校验失败：builtin:table');

    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:list', view: 'brokenView' }}
        projection={{ status: 'ready', projection: emptyProjection }}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(screen.getByRole('alert').textContent).toContain('boom');

    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:list', view: 'chronicle' }}
        projection={{ status: 'error', message: '网络异常' }}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(screen.getByRole('alert').textContent).toContain('网络异常');
  });

  it('links graphic widget select intent to native builtin:detail with Markdown rendering', () => {
    const onSelect = vi.fn();
    const projection: TemplateViewsProjection = {
      templateId: 'agents-delivery',
      views: {
        deliveryFlow: {
          reviewTier: 'L2',
          defaultSelectedId: 'review-b',
          items: [
            { id: 'design', title: '设计', status: 'done', summary: '详见 [架构文档](docs/arch.md)' },
            {
              id: 'review-b',
              title: 'Review B · 前端与契约',
              status: 'blocked',
              summary: '拦截 2 项边界问题',
              callouts: { title: '待处理', items: ['空态需先判空再校验'] },
              entries: { title: '处理日志', items: [{ tag: 'Review', status: 'warn', statusLabel: '待修订', actor: 'Riley', summary: '首轮打回' }] },
            },
          ],
        },
      },
      widgets: { './widgets/delivery-flow.html': '<!doctype html><html><body>dag</body></html>' },
      dataRev: 0,
      flowRev: 0,
      diagnostics: [],
      humanReadOnly: true,
    };

    const { rerender } = render(
      <>
        <ReportWidgetBlock
          payload={{ use: './widgets/delivery-flow.html', view: 'deliveryFlow', selectionKey: 'node' }}
          projection={projection}
          onSelect={onSelect}
          renderText={(text) => <span>{text}</span>}
        />
        <ReportWidgetBlock
          payload={{ use: 'builtin:detail', view: 'deliveryFlow', selectionKey: 'node' }}
          projection={projection}
          renderText={(text) => <span>{text}</span>}
          renderMarkdown={(md) => <div data-testid="native-md">{md}</div>}
        />
      </>,
    );

    expect(screen.getByTestId('native-md').textContent).toContain('拦截 2 项边界问题');
    expect(screen.getByRole('region', { name: '待处理' }).textContent).toContain('空态需先判空再校验');
    const entries = screen.getByRole('region', { name: '处理日志' });
    expect(entries.textContent).toContain('Review');
    expect(entries.textContent).toContain('[Riley] 首轮打回');

    const iframe = screen.getByTitle<HTMLIFrameElement>('deliveryFlow');
    fireEvent(window, new MessageEvent('message', {
      source: iframe.contentWindow,
      data: { type: 'perga:intent', kind: 'select', key: 'node', value: 'design' },
    }));
    expect(onSelect).toHaveBeenCalledWith('node', 'design');

    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:detail', view: 'deliveryFlow', selectionKey: 'node' }}
        projection={projection}
        selectedId="design"
        renderText={(text) => <span>{text}</span>}
        renderMarkdown={(md) => <div data-testid="native-md">{md}</div>}
      />,
    );
    expect(screen.getByTestId('native-md').textContent).toContain('[架构文档](docs/arch.md)');

    const blankFallbackProjection: TemplateViewsProjection = {
      ...projection,
      views: {
        deliveryFlow: {
          items: [{
            id: 'node-blank',
            title: '空白回退校验',
            status: 'done',
            triggers: [{ trigger: '不应展示', action: 'legacy', assign: 'cli' }],
            callouts: { title: '   ', items: ['保留有效提示'] },
            entries: { title: '', items: [{ tag: 'E1', status: 'warn', statusLabel: '   ', actor: '  ', summary: '无参与者记录' }] },
          }],
        },
      },
    };
    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:detail', view: 'deliveryFlow', selectionKey: 'node' }}
        projection={blankFallbackProjection}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(screen.queryByText('不应展示')).toBeNull();
    expect(screen.getByRole('region', { name: '关注事项' }).textContent).toContain('保留有效提示');
    const fallbackEntries = screen.getByRole('region', { name: '记录' });
    expect(fallbackEntries.textContent).toContain('待关注');
    expect(fallbackEntries.textContent).not.toContain('[]');
    expect(fallbackEntries.textContent).toContain('无参与者记录');
  });

  it('dispatches builtin:flow widget from projection.flow and handles empty flow cleanly', () => {
    const projection: TemplateViewsProjection = {
      templateId: '',
      views: {},
      widgets: {},
      dataRev: 0,
      flowRev: 2,
      flow: {
        rev: 2,
        nodes: [
          {
            id: 'build',
            title: '编译与单测',
            source: 'agent',
            whenKind: 'immediate',
            after: [],
            doKind: 'run',
            canceled: false,
            status: 'running',
            outcome: 'none',
            runCount: 1,
            firedCount: 0,
            waitingOn: [],
            gates: [],
          },
        ],
        archived: [],
      },
      diagnostics: [{ view: 'flow', message: 'Unrelated template JS view failure' }],
      humanReadOnly: false,
    };

    const { rerender } = render(
      <ReportWidgetBlock
        payload={{ use: 'builtin:flow', view: 'flow', title: '交付流水线' }}
        projection={{ status: 'ready', projection }}
        renderText={(text) => <span>{text}</span>}
      />,
    );

    expect(screen.getByRole('region', { name: '交付流水线' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: '取消节点' })).toBeNull();

    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:flow', view: 'flow', title: '空流程' }}
        projection={{ status: 'ready', projection: { ...projection, flow: { rev: 2, nodes: [], archived: [] } } }}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(screen.getByRole('note').textContent).toContain('当前暂无活跃或已归档的流程步骤。');
  });

  it('auto-renders flow in ReportDocument for template-less tracks without duplicating explicit builtin:flow', () => {
    const projection: TemplateViewsProjection = {
      templateId: '',
      views: {},
      widgets: {},
      dataRev: 0,
      flowRev: 1,
      flow: {
        rev: 1,
        nodes: [
          {
            id: 'step-1',
            title: '零配置自动调度步骤',
            source: 'agent',
            whenKind: 'immediate',
            after: [],
            doKind: 'run',
            canceled: false,
            status: 'running',
            outcome: 'none',
            runCount: 1,
            firedCount: 0,
            waitingOn: [],
            gates: [],
          },
        ],
        archived: [],
      },
      diagnostics: [{ view: 'flow', message: 'Unrelated template JS view failure' }],
      humanReadOnly: false,
    };

    const templateLessReport: TrackReport = {
      summary: '',
      body: '',
      blocks: [
        { id: 'b_target', kind: 'prose', payload: { markdown: '# Target\n\nShip Unified Track Flow.' } },
        { id: 'b_notes', kind: 'prose', payload: { markdown: '# Notes\n\nDetails below.' } },
      ],
    };

    const { container, rerender } = render(
      <ReportDocument
        report={templateLessReport}
        empty={<p>Empty</p>}
        templateViews={{ status: 'ready', projection }}
      />,
    );

    // Auto-rendered right after `# Target` and before `# Notes`
    const rows = [...container.querySelectorAll('[data-nc-report] > div')];
    const targetIdx = rows.findIndex((el) => el.textContent?.includes('Ship Unified Track Flow.'));
    const flowIdx = rows.findIndex((el) => el.hasAttribute('data-nc-auto-flow'));
    const notesIdx = rows.findIndex((el) => el.textContent?.includes('Details below.'));
    expect(targetIdx).toBeGreaterThanOrEqual(0);
    expect(flowIdx).toBe(targetIdx + 1);
    expect(notesIdx).toBeGreaterThan(flowIdx);
    expect(screen.getAllByRole('region', { name: '调度流程' })).toHaveLength(1);

    // When an explicit `builtin:flow` widget block exists, do NOT duplicate auto-flow
    const explicitFlowReport: TrackReport = {
      summary: '',
      body: '',
      blocks: [
        { id: 'b_target', kind: 'prose', payload: { markdown: '# Target\n\nExplicit flow.' } },
        { id: 'b_flow', kind: 'widget', payload: { use: 'builtin:flow', view: 'flow', title: '显式流程' } },
      ],
    };

    rerender(
      <ReportDocument
        report={explicitFlowReport}
        empty={<p>Empty</p>}
        templateViews={{ status: 'ready', projection }}
      />,
    );

    expect(container.querySelector('[data-nc-auto-flow]')).toBeNull();
    expect(screen.getAllByRole('region', { name: '显式流程' })).toHaveLength(1);
  });
});
