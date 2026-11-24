import type { ReactNode } from 'react';
import type { ReportTaskRow } from '../../../../../core/domain/track-task-inventory.ts';
import type {
  TemplateViewsProjection,
  TemplateViewsState,
  WidgetBlockPayload,
} from '../../../../../core/domain/report-widget.ts';
import {
  parseBuiltinListView,
  parseBuiltinTableView,
} from '../../../../../core/domain/report-widget-compat.ts';
import { EditorialChronicle } from '../editorial/chronicle.tsx';
import { ReportFlowBlock } from '../flow/public.tsx';
import { EditorialTable } from '../editorial/table.tsx';
import { SeamlessHtmlWidget } from './seamless-html.tsx';
import styles from './widget.module.css';

function isTemplateViewsState(
  value: TemplateViewsProjection | TemplateViewsState | undefined,
): value is TemplateViewsState {
  return value !== undefined && 'status' in value
    && (value.status === 'loading' || value.status === 'error' || value.status === 'ready');
}

function WidgetContractError({
  payload,
  issues,
}: Readonly<{
  payload: WidgetBlockPayload;
  issues: readonly { path: readonly PropertyKey[]; message: string }[];
}>) {
  return (
    <div className={styles.diagnostic} role="alert">
      <div className={styles.diagnosticTitle}>视图契约校验失败：{payload.use} ({payload.view})</div>
      <div>{issues.map((issue) => `${issue.path.map(String).join('.') || '$'}: ${issue.message}`).join('；')}</div>
    </div>
  );
}

export function ReportWidgetBlock({
  payload,
  projection: rawProjection,
  taskRows,
  renderText,
  renderMarkdown,
  onOpenFile,
  onOpenWorker,
  onOpenTrack,
}: Readonly<{
  payload: WidgetBlockPayload;
  projection?: TemplateViewsProjection | TemplateViewsState;
  taskRows?: readonly ReportTaskRow[];
  renderText: (text: string) => ReactNode;
  renderMarkdown?: (markdown: string, key: string) => ReactNode;
  onOpenFile?: (path: string) => void;
  onOpenWorker?: (sessionId: string) => void;
  onOpenTrack?: (trackId: string) => void;
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

  // Native Unified Track Flow (`builtin:flow`) reads directly from `projection.flow`,
  // independent of template JS view diagnostics.
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
        renderText={renderText}
        renderMarkdown={renderMarkdown}
        onOpenWorker={onOpenWorker}
      />
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

  if (payload.use === 'builtin:list') {
    const parsed = parseBuiltinListView(viewData);
    if (!parsed.success) {
      return <WidgetContractError payload={payload} issues={parsed.error.issues} />;
    }
    if (parsed.data.items.length === 0 && parsed.legacyMetricsTable === undefined) {
      return (
        <div className={styles.diagnostic} role="note">
          <div className={styles.diagnosticTitle}>{payload.title ?? payload.view}</div>
          <div>当前暂无记录。</div>
        </div>
      );
    }

    return (
      <>
        {parsed.legacyMetricsTable !== undefined && (
          <EditorialTable
            payload={parsed.legacyMetricsTable}
            title={payload.title?.trim() || payload.view}
            renderText={renderText}
            renderMarkdown={renderMarkdown}
          />
        )}
        {parsed.data.items.length > 0 && (
          <EditorialChronicle
            payload={parsed.data}
            taskRows={taskRows}
            renderText={renderText}
            renderMarkdown={renderMarkdown}
            onOpenWorker={onOpenWorker}
          />
        )}
      </>
    );
  }

  if (payload.use === 'builtin:table') {
    const parsed = parseBuiltinTableView(viewData);
    if (!parsed.success) {
      return <WidgetContractError payload={payload} issues={parsed.error.issues} />;
    }
    return (
      <EditorialTable
        payload={parsed.data}
        title={payload.title?.trim() || payload.view}
        renderText={renderText}
        renderMarkdown={renderMarkdown}
      />
    );
  }

  if (payload.use.startsWith('builtin:')) {
    return (
      <div className={styles.diagnostic} role="note">
        <div className={styles.diagnosticTitle}>不支持的内置视图：{payload.use}</div>
      </div>
    );
  }

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
      onOpenFile={onOpenFile}
      onOpenWorker={onOpenWorker}
      onOpenTrack={onOpenTrack}
    />
  );
}
