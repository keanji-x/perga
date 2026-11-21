import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { TemplateViewsProjection } from '../../../../../core/domain/report-widget.ts';
import { ReportWidgetBlock } from './public.tsx';

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
});
