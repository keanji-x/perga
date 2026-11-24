import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TemplateViewsProjection } from '../../../../../core/domain/report-widget.ts';
import type { TrackReport } from '../../../../../core/domain/report.ts';
import { ReportDocument } from '../document/public.tsx';
import { ReportWidgetBlock } from './public.tsx';

afterEach(() => { cleanup(); });

describe('ReportWidgetBlock', () => {
  it('renders builtin:list and builtin:table natively from projected views', () => {
    const projection: TemplateViewsProjection = {
      templateId: 'demo',
      views: {
        chronicleView: {
          summary: '交付记录',
          groups: ['进行中', '已完成'],
          items: [
            { id: 'design', title: '设计', group: '已完成', status: 'done', summary: '完成架构设计' },
            {
              id: 'impl', title: '实现', group: '进行中', status: 'running', summary: '正在编码',
              subtitle: '本周交付', badge: '复核中', tone: 'accent', meta: 'Reviewer', sessionId: 'ses_review',
              note: '等候验收', checks: [{ label: 'Build', status: 'pass' }],
              table: { title: 'Review evidence', columns: [{ key: 'result', label: 'Result' }], rows: [{ result: 'Passed' }] },
            },
          ],
        },
        krView: {
          title: 'Service latency',
          summary: 'Latency by release',
          columns: [
            { key: 'result', label: 'Service' },
            { key: 'current', label: 'Current', align: 'right' },
            { key: 'target', label: 'Target', align: 'center' },
            { key: 'updatedAt', label: 'Updated' },
          ],
          rows: [{
            result: { code: 'KR1', text: '**P95 latency**', markdown: true, tag: 'CI' },
            current: { text: '7.2 min', tone: 'good', check: true },
            updatedAt: '11-21 09:00',
          }],
        },
      },
      widgets: {},
      dataRev: 0,
      flowRev: 0,
      diagnostics: [],
    };

    const onOpenWorker = vi.fn();
    const { rerender } = render(
      <ReportWidgetBlock
        payload={{ use: 'builtin:list', view: 'chronicleView' }}
        projection={projection}
        renderText={(text) => <span>{text}</span>}
        onOpenWorker={onOpenWorker}
      />,
    );
    expect(screen.getByText('交付记录')).toBeTruthy();
    expect(screen.getAllByRole('heading', { level: 4 }).map((heading) => heading.textContent)).toEqual(['进行中', '已完成']);
    expect(screen.getByText('设计')).toBeTruthy();
    expect(screen.getByText('正在编码')).toBeTruthy();
    expect(screen.getByText('本周交付')).toBeTruthy();
    expect(screen.getByText('复核中').dataset.ncTone).toBe('accent');
    expect(screen.getByText('等候验收')).toBeTruthy();
    expect(screen.getByText('Build')).toBeTruthy();
    expect(within(screen.getByRole('table', { name: 'Review evidence' })).getByText('Passed')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Reviewer' }));
    expect(onOpenWorker).toHaveBeenCalledWith('ses_review');

    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:table', view: 'krView' }}
        projection={projection}
        renderText={(text) => <span>{text}</span>}
        renderMarkdown={(markdown) => <p data-markdown>{markdown}</p>}
      />,
    );
    const table = within(screen.getByRole('table', { name: 'Service latency' }));
    expect(screen.getByText('Latency by release').hasAttribute('data-markdown')).toBe(true);
    expect(table.getAllByRole('columnheader').map((header) => header.textContent))
      .toEqual(['Service', 'Current', 'Target', 'Updated']);
    expect(table.getByText('KR1').tagName).toBe('CODE');
    expect(table.getByText('CI')).toBeTruthy();
    expect(table.getByText('**P95 latency**').hasAttribute('data-markdown')).toBe(true);
    expect(table.getByText('11-21 09:00')).toBeTruthy();
    expect(table.getByRole('img', { name: '已通过' })).toBeTruthy();
    expect(table.getByText('7.2 min').closest('td')?.dataset.ncAlign).toBe('right');
    expect(table.getByRole('cell', { name: '—' }).dataset.ncAlign).toBe('center');
  });

  it('gives untitled embedded list tables an accessible name and renders frozen v1 template payloads', () => {
    const projection: TemplateViewsProjection = {
      templateId: 'legacy-frozen',
      views: {
        legacyLedger: {
          metrics: [{
            code: 'KR1',
            label: 'P95 latency',
            tag: 'CI',
            current: '7.2 min',
            targetLabel: '≤ 8 min',
            status: 'met',
            updatedAt: '11-21 09:00',
          }],
        },
        legacyChronicle: {
          metrics: [{ label: 'Headline KR', current: '100%', targetLabel: '100%', status: 'met' }],
          items: [{
            id: 'q3',
            date: '2026-11-21',
            title: 'Q3 Earnings',
            status: 'done',
            verdict: '超预期',
            assign: 'Analyst',
            assignKind: 'model',
            triggerAt: 'After close',
            summary: 'Beat guidance',
            expectations: [{ label: 'Revenue', expected: '$35B', actual: '$36B', status: 'beat' }],
          }],
        },
        blankEmbeddedTitle: {
          items: [{
            id: 'blank-title',
            title: 'Blank table title fallback',
            status: 'running',
            table: { title: '   ', columns: [{ key: 'k', label: 'K' }], rows: [{ k: 'V' }] },
          }],
        },
      },
      widgets: {},
      dataRev: 1,
      flowRev: 0,
      diagnostics: [],
    };

    const { rerender } = render(
      <ReportWidgetBlock
        payload={{ use: 'builtin:table', view: 'legacyLedger', title: '   ' }}
        projection={projection}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    const ledger = within(screen.getByRole('table', { name: 'legacyLedger' }));
    expect(ledger.getByText('KR1')).toBeTruthy();
    expect(ledger.getByText('7.2 min')).toBeTruthy();
    expect(ledger.getByText('≤ 8 min')).toBeTruthy();

    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:list', view: 'legacyChronicle', title: '事件清单' }}
        projection={projection}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(within(screen.getByRole('table', { name: '事件清单' })).getByText('Headline KR')).toBeTruthy();
    expect(screen.getByText('After close')).toBeTruthy();
    expect(screen.getByText('Analyst')).toBeTruthy();
    const embedded = within(screen.getByRole('table', { name: 'Q3 Earnings' }));
    expect(embedded.getByText('Revenue')).toBeTruthy();
    expect(embedded.getByText('$36B')).toBeTruthy();

    cleanup();
    render(
      <ReportWidgetBlock
        payload={{ use: 'builtin:list', view: 'blankEmbeddedTitle' }}
        projection={projection}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(within(screen.getByRole('table', { name: 'Blank table title fallback' })).getByText('V')).toBeTruthy();
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
      data: { type: 'perga:intent', kind: 'open_file', path: 'research-notes.md' },
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
      data: { type: 'perga:intent', kind: 'open_session', sessionId: 'ses_research_1' },
    }));

    expect(onOpenFile).toHaveBeenCalledTimes(1);
    expect(onOpenFile).toHaveBeenCalledWith('research-notes.md');
    expect(onOpenWorker).toHaveBeenCalledWith('ses_research_1');
  });

  it('opens only HTTP(S) URLs and well-formed Track IDs from the widget frame', () => {
    const onOpenTrack = vi.fn();
    const open = vi.spyOn(window, 'open').mockReturnValue(null);
    try {
      render(
        <ReportWidgetBlock
          payload={{ use: './widget.html', view: 'test' }}
          projection={{ templateId: 'demo', views: { test: {} }, widgets: { './widget.html': '<p>Widget</p>' },
            dataRev: 0, flowRev: 0, diagnostics: [] }}
          renderText={(text) => text}
          onOpenTrack={onOpenTrack}
        />,
      );
      const frame = screen.getByTitle<HTMLIFrameElement>('test');
      const send = (intent: Record<string, string>, source: Window | null = frame.contentWindow) => {
        fireEvent(window, new MessageEvent('message', { source, data: { type: 'perga:intent', ...intent } }));
      };
      send({ kind: 'open_url', url: 'https://example.com' });
      send({ kind: 'open_url', url: 'http://example.com/docs' });
      for (const url of ['javascript:alert(1)', 'file:///readme', '/relative', '//example.com']) {
        send({ kind: 'open_url', url });
      }
      send({ kind: 'open_url', url: 'https://example.com/spoofed' }, window);
      expect(open.mock.calls).toEqual([
        ['https://example.com/', '_blank', 'noopener,noreferrer'],
        ['http://example.com/docs', '_blank', 'noopener,noreferrer'],
      ]);

      send({ kind: 'open_track', trackId: 'trk_abc-123_X' });
      for (const trackId of ['trk_', 'ses_wrong', 'trk_invalid/path', ' trk_abc']) {
        send({ kind: 'open_track', trackId });
      }
      send({ kind: 'open_track', trackId: 'trk_spoofed' }, window);
      expect(onOpenTrack.mock.calls).toEqual([['trk_abc-123_X']]);
    } finally {
      open.mockRestore();
    }
  });

  it('renders empty builtin:list gracefully and surfaces schema/loading/error states', () => {
    const emptyProjection: TemplateViewsProjection = {
      templateId: 'equity-research',
      views: {
        chronicle: { items: [] },
        emptyLedger: { columns: [{ key: 'cost', label: 'Cost' }], rows: [] },
        badLedger: { columns: [{ key: 'cost', label: 123 }], rows: [] },
        badChronicle: { items: [{ id: 'bad', title: 'Broken', status: 'unknown' }] },
      },
      widgets: {},
      dataRev: 4,
      flowRev: 0,
      diagnostics: [{ view: 'brokenView', message: 'boom' }],
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
    expect(screen.getByText('当前暂无可展示的行。')).toBeTruthy();

    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:table', view: 'badLedger' }}
        projection={{ status: 'ready', projection: emptyProjection }}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(screen.getByRole('alert').textContent).toContain('视图契约校验失败：builtin:table');
    expect(screen.getByRole('alert').textContent).toContain('columns.0.label');

    rerender(
      <ReportWidgetBlock
        payload={{ use: 'builtin:list', view: 'badChronicle' }}
        projection={emptyProjection}
        renderText={(text) => <span>{text}</span>}
      />,
    );
    expect(screen.getByRole('alert').textContent).toContain('视图契约校验失败：builtin:list');
    expect(screen.getByRole('alert').textContent).toContain('items.0.status');

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
            repeat: false,
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
            repeat: false,
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
    expect(screen.getAllByRole('region', { name: 'Flow' })).toHaveLength(1);

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
