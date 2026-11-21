// Scheme B Unified VO Block dispatcher:
//   - `builtin:stepper` / `builtin:list` / `builtin:table` / `builtin:detail`
//       -> Native React editorial skeletons (0ms, Ctrl+F, serif ProseBlock, zero white box)
//   - `./widgets/*.html`
//       -> Seamless Data-Bound HTML Graphic Widget (closed Bridge, transparent bg, exact height,
//          can drive a paired `builtin:detail` block via `selectionKey` + `perga:intent { kind: 'select' }`)

import type { ReactNode } from 'react';
import {
  checkpointItemSchema,
  checkpointsBlockPayloadSchema,
  type CheckpointMetric,
} from '../../../../../core/domain/report-checkpoints.ts';
import type { ReportTaskRow } from '../../../../../core/domain/report.ts';
import type {
  TemplateViewsProjection,
  TemplateViewsState,
  WidgetBlockPayload,
} from '../../../../../core/domain/report-widget.ts';
import { useState } from '../../../ui/state/public.ts';
import { ReportCheckpointsBlock } from '../checkpoints/public.tsx';
import { ReportFlowBlock } from '../flow/public.tsx';
import { composeItemMarkdown, EditorialByline, EditorialGateStrip } from '../editorial/primitives.tsx';
import { EditorialMetricsStrip } from '../editorial/metrics.tsx';
import { SeamlessHtmlWidget } from './seamless-html.tsx';
import styles from './widget.module.css';

function isTemplateViewsState(
  value: TemplateViewsProjection | TemplateViewsState | undefined,
): value is TemplateViewsState {
  return value !== undefined && 'status' in value
    && (value.status === 'loading' || value.status === 'error' || value.status === 'ready');
}

type DetailEntry = Readonly<{
  tag: string;
  status: 'pass' | 'warn' | 'fail' | 'running' | 'neutral';
  statusLabel?: string;
  actor?: string;
  summary: string;
}>;

function nonEmptyString(raw: unknown): string | undefined {
  if (typeof raw !== 'string') return undefined;
  const trimmed = raw.trim();
  return trimmed !== '' ? trimmed : undefined;
}

function parseEntries(raw: unknown): readonly DetailEntry[] {
  if (!Array.isArray(raw)) return [];
  const out: DetailEntry[] = [];
  for (const entry of raw) {
    if (entry === null || typeof entry !== 'object') continue;
    const rec = entry as Record<string, unknown>;
    const tag = nonEmptyString(rec.tag);
    const summary = nonEmptyString(rec.summary);
    if (tag === undefined || summary === undefined) continue;
    const status = rec.status === 'pass' || rec.status === 'warn' || rec.status === 'fail' || rec.status === 'running'
      ? rec.status : 'neutral';
    out.push({ tag, status, summary,
      statusLabel: nonEmptyString(rec.statusLabel),
      actor: nonEmptyString(rec.actor) });
  }
  return out;
}

function detailSection(raw: unknown): Record<string, unknown> {
  return raw !== null && typeof raw === 'object' ? raw as Record<string, unknown> : {};
}

function entryStatus(status: DetailEntry['status']): { label: string; className: string } {
  switch (status) {
    case 'pass': return { label: '通过', className: styles.detailEntryPass };
    case 'fail': return { label: '失败', className: styles.detailEntryFail };
    case 'running': return { label: '进行中', className: styles.detailEntryRun };
    case 'warn': return { label: '待关注', className: styles.detailEntryWarn };
    case 'neutral': return { label: '记录', className: styles.detailEntryNeutral };
  }
}

function NativeDetailWidget({
  rawObj,
  selectedId,
  taskRows,
  renderText,
  renderMarkdown,
  onOpenWorker,
}: Readonly<{
  rawObj: Record<string, unknown>;
  selectedId?: string;
  taskRows?: readonly ReportTaskRow[];
  renderText: (text: string) => ReactNode;
  renderMarkdown?: (markdown: string, key: string) => ReactNode;
  onOpenWorker?: (sessionId: string) => void;
}>) {
  const [activeGateKey, setActiveGateKey] = useState<string | null>(null);
  const rawItems: readonly unknown[] = Array.isArray(rawObj.items) ? rawObj.items : [];
  if (rawItems.length === 0) {
    return (
      <div className={styles.diagnostic} role="note">
        <div>当前暂无详情内容。</div>
      </div>
    );
  }

  const defaultId = typeof rawObj.defaultSelectedId === 'string' ? rawObj.defaultSelectedId : undefined;
  const pickRaw: unknown = rawItems.find((entry) =>
    entry !== null && typeof entry === 'object' && (entry as Record<string, unknown>).id === selectedId)
    ?? rawItems.find((entry) =>
      entry !== null && typeof entry === 'object' && (entry as Record<string, unknown>).id === defaultId)
    ?? rawItems[0];

  const pickRecord = (pickRaw !== null && typeof pickRaw === 'object' ? pickRaw : {}) as Record<string, unknown>;
  const knownItemFields: Record<string, unknown> = {};
  for (const key of [
    'id', 'title', 'status', 'statusLabel', 'role', 'taskKey', 'sessionId',
    'model', 'assign', 'assignKind', 'workerLabel', 'summary', 'bullets',
    'triggerAt', 'docLabel', 'docPath', 'blockedReason', 'dependsOn',
    'constraint', 'checks', 'expectations',
  ]) {
    if (pickRecord[key] !== undefined) knownItemFields[key] = pickRecord[key];
  }

  const parsedItem = checkpointItemSchema.safeParse(knownItemFields);
  if (!parsedItem.success) {
    return (
      <div className={styles.diagnostic} role="alert">
        <div className={styles.diagnosticTitle}>详情视图契约校验失败</div>
        <div>{parsedItem.error.issues.map((i) => `${i.path.join('.') || '$'}: ${i.message}`).join('；')}</div>
      </div>
    );
  }

  const item = parsedItem.data;
  const calloutSection = detailSection(pickRecord.callouts);
  const callouts = Array.isArray(calloutSection.items)
    ? calloutSection.items.filter((entry): entry is string => typeof entry === 'string' && entry.trim() !== '') : [];
  const calloutsTitle = nonEmptyString(calloutSection.title) ?? '关注事项';
  const entrySection = detailSection(pickRecord.entries);
  const entries = parseEntries(entrySection.items);
  const entriesTitle = nonEmptyString(entrySection.title) ?? '记录';
  const bodyMarkdown = composeItemMarkdown(item);

  return (
    <article className={styles.detailArticle} aria-live="polite" aria-label={item.title}>
      <header className={styles.detailHeader}>
        <h3 className={styles.detailTitle}>{item.docLabel ?? item.title}</h3>
        <EditorialByline item={item} taskRows={taskRows} onOpenWorker={onOpenWorker} />
      </header>

      {bodyMarkdown !== '' && (
        <div className={styles.detailProse}>
          {renderMarkdown !== undefined
            ? renderMarkdown(bodyMarkdown, `widget-detail-${item.id}`)
            : <p>{renderText(bodyMarkdown)}</p>}
        </div>
      )}

      {callouts.length > 0 && (
        <div className={styles.detailCallouts} role="region" aria-label={calloutsTitle}>
          <div className={styles.detailCalloutsTitle}>{calloutsTitle}</div>
          {callouts.map((callout, idx) => (
            <div key={`${item.id}-co-${idx}`}>• {renderText(callout)}</div>
          ))}
        </div>
      )}

      {entries.length > 0 && (
        <div className={styles.detailEntries} role="region" aria-label={entriesTitle}>
          <div className={styles.detailEntriesHead}>{entriesTitle}（{entries.length}）</div>
          {entries.map((entry, index) => {
            const status = entryStatus(entry.status);
            return (
              <div key={`${entry.tag}-${index}`} className={styles.detailEntryRow}>
                <span className={styles.detailEntryTag}>{entry.tag}</span>
                <span className={status.className}>{entry.statusLabel ?? status.label}</span>
                <span>{entry.actor !== undefined ? `[${entry.actor}] ` : ''}{renderText(entry.summary)}</span>
              </div>
            );
          })}
        </div>
      )}

      {item.checks != null && item.checks.length > 0 && (
        <EditorialGateStrip
          itemId={`detail-${item.id}`}
          checks={item.checks}
          activeGateKey={activeGateKey}
          onSelectGateKey={setActiveGateKey}
          renderText={renderText}
        />
      )}
    </article>
  );
}

export function ReportWidgetBlock({
  payload,
  projection: rawProjection,
  taskRows,
  selectedId,
  onSelect,
  renderText,
  renderMarkdown,
  onOpenFile,
  onOpenWorker,
  onCancelFlowNode,
}: Readonly<{
  payload: WidgetBlockPayload;
  projection?: TemplateViewsProjection | TemplateViewsState;
  taskRows?: readonly ReportTaskRow[];
  selectedId?: string;
  onSelect?: (key: string, value: string) => void;
  renderText: (text: string) => ReactNode;
  renderMarkdown?: (markdown: string, key: string) => ReactNode;
  onOpenFile?: (path: string) => void;
  onOpenWorker?: (sessionId: string) => void;
  onCancelFlowNode?: (nodeId: string) => Promise<void> | void;
}>) {
  if (isTemplateViewsState(rawProjection)) {
    if (rawProjection.status === 'loading') {
      return (
        <div className={styles.diagnostic} role="status">
          正在加载视图 {payload.view}…
        </div>
      );
    }
    if (rawProjection.status === 'error') {
      return (
        <div className={styles.diagnostic} role="alert">
          <div className={styles.diagnosticTitle}>视图加载失败：{payload.view}</div>
          <div>{rawProjection.message}</div>
        </div>
      );
    }
  }

  const projection: TemplateViewsProjection | undefined = isTemplateViewsState(rawProjection)
    ? rawProjection.projection
    : rawProjection;

  if (projection === undefined) {
    return (
      <div className={styles.diagnostic} role="note">
        <div className={styles.diagnosticTitle}>实时视图：{payload.title ?? payload.view}</div>
        <div>请在 Track 页面查看完整视图投影。</div>
      </div>
    );
  }

  const diag = projection.diagnostics.find((d) => d.view === payload.view);
  if (diag !== undefined) {
    return (
      <div className={styles.diagnostic} role="alert">
        <div className={styles.diagnosticTitle}>视图投影失败：{payload.view}</div>
        <div>{diag.message}</div>
      </div>
    );
  }

  // Native Unified Track Flow (`builtin:flow`) reads directly from `projection.flow`.
  if (payload.use === 'builtin:flow') {
    const flow = projection.flow;
    if (flow == null || (flow.nodes.length === 0 && flow.archived.length === 0)) {
      return (
        <div className={styles.diagnostic} role="note">
          <div className={styles.diagnosticTitle}>{payload.title ?? '调度流程'}</div>
          <div>当前暂无活跃或已归档的流程步骤。</div>
        </div>
      );
    }
    return (
      <ReportFlowBlock
        flow={flow}
        title={payload.title ?? undefined}
        taskRows={taskRows}
        renderText={renderText}
        renderMarkdown={renderMarkdown}
        onOpenWorker={onOpenWorker}
        onCancelFlowNode={onCancelFlowNode}
      />
    );
  }

  const viewData = projection.views[payload.view];
  if (viewData === undefined) {
    return (
      <div className={styles.diagnostic} role="note">
        <div className={styles.diagnosticTitle}>未找到视图：{payload.view}</div>
        <div>模板未声明该视图或数据尚未就绪。</div>
      </div>
    );
  }

  // Track 1: Native React Editorial Skeletons (`builtin:stepper`, `builtin:list`, `builtin:table`, `builtin:detail`)
  if (payload.use === 'builtin:detail') {
    const rawObj = viewData !== null && typeof viewData === 'object' ? (viewData as Record<string, unknown>) : {};
    return (
      <NativeDetailWidget
        rawObj={rawObj}
        selectedId={selectedId}
        taskRows={taskRows}
        renderText={renderText}
        renderMarkdown={renderMarkdown}
        onOpenWorker={onOpenWorker}
      />
    );
  }

  if (payload.use === 'builtin:stepper' || payload.use === 'builtin:list') {
    const rawObj = viewData !== null && typeof viewData === 'object' ? (viewData as Record<string, unknown>) : {};
    if (Array.isArray(rawObj.items) && rawObj.items.length === 0) {
      return (
        <div className={styles.diagnostic} role="note">
          <div className={styles.diagnosticTitle}>{payload.title ?? payload.view}</div>
          <div>当前暂无记录。</div>
        </div>
      );
    }

    // Schedules are shown from persisted track schedules, not widget declarations.
    const mergedPayload = {
      ...rawObj,
      layout: payload.use === 'builtin:stepper' ? 'stepper' : 'chronicle',
      triggers: undefined,
    };
    const parsed = checkpointsBlockPayloadSchema.safeParse(mergedPayload);
    if (!parsed.success) {
      return (
        <div className={styles.diagnostic} role="alert">
          <div className={styles.diagnosticTitle}>视图契约校验失败：{payload.use} ({payload.view})</div>
          <div>{parsed.error.issues.map((i) => `${i.path.join('.') || '$'}: ${i.message}`).join('；')}</div>
        </div>
      );
    }

    return (
      <ReportCheckpointsBlock
        payload={parsed.data}
        taskRows={taskRows}
        renderText={renderText}
        renderMarkdown={renderMarkdown}
        onOpenWorker={onOpenWorker}
      />
    );
  }

  if (payload.use === 'builtin:table') {
    const rawObj = viewData !== null && typeof viewData === 'object' ? (viewData as Record<string, unknown>) : {};
    if (!Array.isArray(rawObj.metrics)) {
      return (
        <div className={styles.diagnostic} role="alert">
          <div className={styles.diagnosticTitle}>视图契约校验失败：builtin:table ({payload.view})</div>
          <div>缺少 metrics 数组字段。</div>
        </div>
      );
    }
    if (rawObj.metrics.length === 0) {
      return (
        <div className={styles.diagnostic} role="note">
          <div className={styles.diagnosticTitle}>空指标总账：{payload.view}</div>
          <div>当前暂无可展示条目。</div>
        </div>
      );
    }
    const parsed = checkpointsBlockPayloadSchema.safeParse({
      items: [{ id: '_table', title: '_table', status: 'done' }],
      metrics: rawObj.metrics,
    });
    if (!parsed.success) {
      return (
        <div className={styles.diagnostic} role="alert">
          <div className={styles.diagnosticTitle}>视图契约校验失败：builtin:table ({payload.view})</div>
          <div>{parsed.error.issues.map((i) => `${i.path.join('.') || '$'}: ${i.message}`).join('；')}</div>
        </div>
      );
    }
    const parsedMetrics: readonly CheckpointMetric[] = parsed.data.metrics ?? [];

    return <EditorialMetricsStrip metrics={parsedMetrics} renderText={renderText} renderMarkdown={renderMarkdown} />;
  }

  // Track 2: Seamless Data-Bound HTML Graphic Widget (`./widgets/*.html`)
  const normalizedKey = payload.use.replace(/^\.\//, '');
  const html = projection.widgets[payload.use] ?? projection.widgets[normalizedKey];
  if (html === undefined) {
    return (
      <div className={styles.diagnostic} role="alert">
        <div className={styles.diagnosticTitle}>未找到 HTML Widget：{payload.use}</div>
        <div>请确认该文件存在于模板包目录中。</div>
      </div>
    );
  }

  return (
    <SeamlessHtmlWidget
      html={html}
      title={payload.title ?? payload.view}
      data={viewData}
      chrome={payload.chrome ?? 'seamless'}
      initialHeight={payload.height ?? 140}
      selectedId={selectedId}
      selectionKey={payload.selectionKey}
      onSelect={onSelect}
      onOpenFile={onOpenFile}
      onOpenWorker={onOpenWorker}
    />
  );
}
