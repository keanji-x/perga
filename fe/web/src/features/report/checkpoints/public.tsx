import type { ReactNode } from 'react';
import type {
  CheckpointItem,
  CheckpointsBlockPayload,
  CheckpointStatus,
} from '../../../../../core/domain/report-checkpoints.ts';
import type { ReportTaskRow } from '../../../../../core/domain/report.ts';
import { useState } from '../../../ui/state/public.ts';
import { ReportCheckpointsChronicle } from './chronicle.tsx';
export { CheckpointMetricsStrip } from './metrics-strip.tsx';
export { CheckpointByline, CheckpointGateStrip, CheckpointTriggersTable } from './shared.tsx';
import {
  CheckpointByline,
  CheckpointGateStrip,
  CheckpointProseBody,
  CheckpointTriggersTable,
  defaultStatusLabel,
  deriveFallbackTriggers,
  markerToneClass,
} from './shared.tsx';
import styles from './checkpoints.module.css';

function connectorToneClass(status: CheckpointStatus): string {
  switch (status) {
    case 'done': return styles.connectorDone;
    case 'running': return styles.connectorRunning;
    case 'blocked':
    case 'pending': return '';
  }
}

function captionToneClass(status: CheckpointStatus): string {
  switch (status) {
    case 'running': return styles.captionRunning;
    case 'blocked': return styles.captionBlocked;
    case 'done':
    case 'pending': return '';
  }
}

function initialSelectedId(items: readonly CheckpointItem[]): string {
  const active = items.find((item) => item.status === 'running' || item.status === 'blocked');
  return active?.id ?? items[0].id;
}

function formatIndex(index: number): string {
  return String(index + 1).padStart(2, '0');
}

export function ReportCheckpointsBlock({
  payload,
  taskRows,
  renderText,
  renderMarkdown,
  onOpenWorker,
}: Readonly<{
  payload: CheckpointsBlockPayload;
  taskRows?: readonly ReportTaskRow[];
  renderText?: (text: string) => ReactNode;
  renderMarkdown?: (markdown: string, key: string) => ReactNode;
  onOpenWorker?: (sessionId: string) => void;
}>) {
  if (payload.layout === 'chronicle') {
    return (
      <ReportCheckpointsChronicle
        payload={payload}
        taskRows={taskRows}
        renderText={renderText}
        renderMarkdown={renderMarkdown}
        onOpenWorker={onOpenWorker}
      />
    );
  }

  return (
    <ReportCheckpointsStepper
      payload={payload}
      taskRows={taskRows}
      renderText={renderText}
      renderMarkdown={renderMarkdown}
      onOpenWorker={onOpenWorker}
    />
  );
}

function ReportCheckpointsStepper({
  payload,
  taskRows,
  renderText,
  renderMarkdown,
  onOpenWorker,
}: Readonly<{
  payload: CheckpointsBlockPayload;
  taskRows?: readonly ReportTaskRow[];
  renderText?: (text: string) => ReactNode;
  renderMarkdown?: (markdown: string, key: string) => ReactNode;
  onOpenWorker?: (sessionId: string) => void;
}>) {
  const [selectedId, setSelectedId] = useState(() => initialSelectedId(payload.items));
  const [activeGateKey, setActiveGateKey] = useState<string | null>(null);
  const selected = payload.items.find((item) => item.id === selectedId) ?? payload.items[0];
  const text = (value: string) => (renderText !== undefined ? renderText(value) : value);

  const checks = selected.checks ?? [];
  const triggers = deriveFallbackTriggers(selected);
  const articleHeadline = selected.docLabel ?? selected.title;

  return (
    <section className={styles.root} aria-label={payload.title ?? '阶段进度'}>
      {/* 1. Publication-style hairline progression rail */}
      <nav className={styles.rail} aria-label="阶段目录">
        <div className={styles.stepper} role="tablist" aria-label="阶段列表">
          {payload.items.map((item, index) => {
            const isSelected = item.id === selected.id;
            const caption = item.statusLabel ?? defaultStatusLabel(item.status);
            const hasConnector = index < payload.items.length - 1;
            return (
              <button
                key={item.id}
                type="button"
                role="tab"
                aria-selected={isSelected}
                className={`${styles.stageButton} ${isSelected ? styles.stageSelected : ''}`}
                onClick={() => {
                  setSelectedId(item.id);
                  setActiveGateKey(null);
                }}
              >
                <span className={styles.stageIndex}>{formatIndex(index)}</span>
                <span className={styles.markerRow} aria-hidden="true">
                  <span className={`${styles.marker} ${markerToneClass(item.status)}`}>
                    {item.status === 'running' && <span className={styles.markerRunningCore} />}
                  </span>
                  {hasConnector && (
                    <span className={`${styles.connector} ${connectorToneClass(item.status)}`} />
                  )}
                </span>
                <span className={styles.stageTitle}>{item.title}</span>
                <span className={`${styles.stageCaption} ${captionToneClass(item.status)}`}>
                  {caption}
                </span>
              </button>
            );
          })}
        </div>
      </nav>

      {/* 2. Publication layout: Headline + Byline -> Prose Body / Schedule Table -> Compact Gate Citations */}
      <article className={styles.article} role="tabpanel" aria-label={selected.title}>
        <header className={styles.articleHeader}>
          <h3 className={styles.articleTitle}>{articleHeadline}</h3>
          <CheckpointByline item={selected} taskRows={taskRows} onOpenWorker={onOpenWorker} />
        </header>

        {selected.blockedReason != null && selected.blockedReason.trim() !== '' && (
          <div className={styles.blockedNote} role="alert">
            {text(selected.blockedReason)}
          </div>
        )}

        <CheckpointProseBody
          item={selected}
          renderText={renderText}
          renderMarkdown={renderMarkdown}
        />

        <CheckpointTriggersTable
          keyPrefix={selected.id}
          triggers={triggers}
          renderText={renderText}
        />

        <CheckpointGateStrip
          itemId={selected.id}
          checks={checks}
          activeGateKey={activeGateKey}
          onSelectGateKey={setActiveGateKey}
          renderText={renderText}
        />
      </article>
    </section>
  );
}
