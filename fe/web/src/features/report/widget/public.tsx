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
  type CheckpointTrigger,
} from '../../../../../core/domain/report-checkpoints.ts';
import type { ReportTaskRow } from '../../../../../core/domain/report.ts';
import type {
  TemplateViewsProjection,
  TemplateViewsState,
  WidgetBlockPayload,
} from '../../../../../core/domain/report-widget.ts';
import { useState } from '../../../ui/state/public.ts';
import {
  CheckpointByline,
  CheckpointGateStrip,
  CheckpointMetricsStrip,
  CheckpointTriggersTable,
  ReportCheckpointsBlock,
} from '../checkpoints/public.tsx';
import { SeamlessHtmlWidget } from './seamless-html.tsx';
import styles from './widget.module.css';

function isTemplateViewsState(
  value: TemplateViewsProjection | TemplateViewsState | undefined,
): value is TemplateViewsState {
  return value !== undefined && 'status' in value
    && (value.status === 'loading' || value.status === 'error' || value.status === 'ready');
}

function triggersFromProjection(projection: TemplateViewsProjection | undefined): readonly CheckpointTrigger[] | undefined {
  if (projection === undefined || projection.triggers.length === 0) return undefined;
  return projection.triggers.map((tr) => ({
    trigger: tr.onSummary ?? tr.onKind,
    action: tr.label,
    assign: tr.assign ?? '—',
    assignKind: tr.assignKind === 'cli' ? 'command' : 'model',
  }));
}

type DetailRound = Readonly<{
  round: number;
  label?: string;
  outcome: 'pass' | 'changes_requested' | 'running';
  outcomeLabel?: string;
  triggeredBy?: string;
  summary: string;
}>;

function parseRounds(raw: unknown): readonly DetailRound[] {
  if (!Array.isArray(raw)) return [];
  const out: DetailRound[] = [];
  for (const entry of raw) {
    if (entry === null || typeof entry !== 'object') continue;
    const rec = entry as Record<string, unknown>;
    if (typeof rec.round !== 'number' || typeof rec.summary !== 'string') continue;
    const outcome = rec.outcome === 'pass' || rec.outcome === 'changes_requested' ? rec.outcome : 'running';
    out.push({
      round: rec.round,
      label: typeof rec.label === 'string' ? rec.label : undefined,
      outcome,
      outcomeLabel: typeof rec.outcomeLabel === 'string' && rec.outcomeLabel.trim() !== '' ? rec.outcomeLabel : undefined,
      triggeredBy: typeof rec.triggeredBy === 'string' ? rec.triggeredBy : undefined,
      summary: rec.summary,
    });
  }
  return out;
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
    'constraint', 'checks', 'triggers', 'expectations',
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
  const blockers = Array.isArray(pickRecord.blockers)
    ? pickRecord.blockers.filter((b): b is string => typeof b === 'string' && b.trim() !== '')
    : [];
  const blockersTitle = typeof pickRecord.blockersTitle === 'string' && pickRecord.blockersTitle.trim() !== ''
    ? pickRecord.blockersTitle
    : (typeof rawObj.blockersTitle === 'string' && rawObj.blockersTitle.trim() !== '' ? rawObj.blockersTitle : '关注事项');
  const rounds = parseRounds(pickRecord.rounds);
  const roundsTitle = typeof pickRecord.roundsTitle === 'string' && pickRecord.roundsTitle.trim() !== ''
    ? pickRecord.roundsTitle
    : (typeof rawObj.roundsTitle === 'string' && rawObj.roundsTitle.trim() !== '' ? rawObj.roundsTitle : '迭代记录');
  const bodyMarkdown = [
    item.summary?.trim() ?? '',
    ...(item.bullets != null && item.bullets.length > 0 ? [item.bullets.map((b) => `- ${b}`).join('\n')] : []),
  ].filter(Boolean).join('\n\n');

  return (
    <article className={styles.detailArticle} aria-live="polite" aria-label={item.title}>
      <header className={styles.detailHeader}>
        <h3 className={styles.detailTitle}>{item.docLabel ?? item.title}</h3>
        <CheckpointByline item={item} taskRows={taskRows} onOpenWorker={onOpenWorker} />
      </header>

      {bodyMarkdown !== '' && (
        <div className={styles.detailProse}>
          {renderMarkdown !== undefined
            ? renderMarkdown(bodyMarkdown, `widget-detail-${item.id}`)
            : <p>{renderText(bodyMarkdown)}</p>}
        </div>
      )}

      {blockers.length > 0 && (
        <div className={styles.detailBlockers} role="region" aria-label={blockersTitle}>
          <div className={styles.detailBlockersTitle}>{blockersTitle}</div>
          {blockers.map((blocker, idx) => (
            <div key={`${item.id}-blk-${idx}`}>• {renderText(blocker)}</div>
          ))}
        </div>
      )}

      {rounds.length > 0 && (
        <div className={styles.detailRounds} role="region" aria-label={roundsTitle}>
          <div className={styles.detailRoundsHead}>{roundsTitle}（{rounds.length}）</div>
          {rounds.map((rd) => (
            <div key={`${item.id}-rd-${rd.round}`} className={styles.detailRoundRow}>
              <span className={styles.detailRoundTag}>R{rd.round}</span>
              <span className={rd.outcome === 'pass'
                ? styles.detailRoundPass
                : rd.outcome === 'changes_requested'
                  ? styles.detailRoundFail
                  : styles.detailRoundRun}
              >
                {rd.outcomeLabel ?? (rd.outcome === 'pass' ? '通过' : rd.outcome === 'changes_requested' ? '待修订' : '进行中')}
              </span>
              <span>{rd.triggeredBy !== undefined ? `[${rd.triggeredBy}] ` : ''}{renderText(rd.summary)}</span>
            </div>
          ))}
        </div>
      )}

      {item.triggers != null && item.triggers.length > 0 && (
        <CheckpointTriggersTable keyPrefix={`detail-${item.id}`} triggers={item.triggers} renderText={renderText} />
      )}

      {item.checks != null && item.checks.length > 0 && (
        <CheckpointGateStrip
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
    const standingTriggers = rawObj.triggers ?? (payload.showTriggers ? triggersFromProjection(projection) : undefined);

    if (Array.isArray(rawObj.items) && rawObj.items.length === 0) {
      const parsedEmpty = checkpointsBlockPayloadSchema.safeParse({
        items: [{ id: '_empty', title: '_empty', status: 'pending' }],
        triggers: standingTriggers,
      });
      const safeTriggers = parsedEmpty.success ? (parsedEmpty.data.triggers ?? []) : [];
      return (
        <div>
          <div className={styles.diagnostic} role="note">
            <div className={styles.diagnosticTitle}>{payload.title ?? payload.view}</div>
            <div>当前暂无记录。</div>
          </div>
          {safeTriggers.length > 0 && (
            <CheckpointTriggersTable keyPrefix={payload.view} triggers={safeTriggers} renderText={renderText} />
          )}
        </div>
      );
    }

    const mergedPayload = {
      ...rawObj,
      layout: payload.use === 'builtin:stepper' ? 'stepper' : 'chronicle',
      triggers: standingTriggers,
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

    return <CheckpointMetricsStrip metrics={parsedMetrics} renderText={renderText} renderMarkdown={renderMarkdown} />;
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
