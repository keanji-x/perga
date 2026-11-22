import type { ReactNode } from 'react';
import type {
  CheckpointItem,
  CheckpointMetric,
  CheckpointTrigger,
} from '../../../../../core/domain/report-checkpoints.ts';
import type { ReportTaskRow } from '../../../../../core/domain/report.ts';
import { useState } from '../../../ui/state/public.ts';
import { EditorialMetricsStrip } from '../editorial/metrics.tsx';
import {
  CheckpointExpectationsTable,
  EditorialGateStrip,
  CheckpointProseBody,
  EditorialTriggersTable,
  defaultStatusLabel,
  deriveFallbackTriggers,
  resolveAuthorDisplay,
  resolveItemSessionId,
} from '../editorial/primitives.tsx';
import { markerToneClass } from './marker.ts';
import styles from './checkpoints.module.css';

const INITIAL_VISIBLE_COUNT = 6;

function initialExpandedId(items: readonly CheckpointItem[]): string {
  const active = items.find((item) => item.status === 'running' || item.status === 'blocked');
  return active?.id ?? items[0].id;
}

function verdictToneClass(item: CheckpointItem): string {
  if (item.status === 'running') return styles.chronicleVerdictRunning;
  if (item.status === 'blocked') return styles.chronicleVerdictBlocked;
  return '';
}

type GroupedBucket = Readonly<{
  group: string | null;
  items: readonly CheckpointItem[];
}>;

function groupVisibleItems(items: readonly CheckpointItem[], groups: readonly string[]): readonly GroupedBucket[] {
  const hasGroups = items.some((item) => item.group != null && item.group.trim() !== '');
  if (!hasGroups) {
    return [{ group: null, items }];
  }
  const byName = new Map<string, CheckpointItem[]>(groups.map((name) => [name.trim(), []]));
  for (const item of items) {
    const name = item.group != null && item.group.trim() !== '' ? item.group.trim() : '其他';
    let list = byName.get(name);
    if (list === undefined) {
      list = [];
      byName.set(name, list);
    }
    list.push(item);
  }
  return Array.from(byName, ([group, entries]) => ({ group, items: entries })).filter((bucket) => bucket.items.length > 0);
}

export function ReportCheckpointsChronicle({
  payload,
  taskRows,
  renderText,
  renderMarkdown,
  onOpenWorker,
}: Readonly<{
  payload: Readonly<{
    items: readonly CheckpointItem[];
    title?: string | null;
    summary?: string | null;
    groups?: readonly string[] | null;
    metrics?: readonly CheckpointMetric[] | null;
    triggers?: readonly CheckpointTrigger[] | null;
  }>;
  taskRows?: readonly ReportTaskRow[];
  renderText?: (text: string) => ReactNode;
  renderMarkdown?: (markdown: string, key: string) => ReactNode;
  onOpenWorker?: (sessionId: string) => void;
}>) {
  const [expandedId, setExpandedId] = useState<string | null>(() => initialExpandedId(payload.items));
  const [showAll, setShowAll] = useState(false);
  const [showTriggers, setShowTriggers] = useState(false);
  const [activeGateKey, setActiveGateKey] = useState<string | null>(null);

  const text = (value: string) => (renderText !== undefined ? renderText(value) : value);
  const standingTriggers = payload.triggers ?? [];

  const hasGroups = payload.items.some((item) => item.group != null && item.group.trim() !== '');
  const visibleItems = (showAll || hasGroups)
    ? payload.items
    : payload.items.slice(0, INITIAL_VISIBLE_COUNT);
  const hiddenCount = payload.items.length - visibleItems.length;
  const buckets = groupVisibleItems(visibleItems, payload.groups ?? []);

  return (
    <section className={styles.root} aria-label={payload.title ?? '事件时间线'}>
      {payload.summary != null && payload.summary !== '' && (
        renderMarkdown?.(payload.summary, 'chronicle-summary') ?? <p>{text(payload.summary)}</p>
      )}
      {payload.metrics != null && payload.metrics.length > 0 && (
        <EditorialMetricsStrip
          metrics={payload.metrics}
          renderText={renderText}
          renderMarkdown={renderMarkdown}
        />
      )}

      {standingTriggers.length > 0 && (
        <div className={styles.chronicleBar}>
          <button
            type="button"
            aria-expanded={showTriggers}
            className={`${styles.watchToggle} ${showTriggers ? styles.watchToggleActive : ''}`}
            onClick={() => { setShowTriggers((open) => !open); }}
          >
            自动监测 · {standingTriggers.length}
          </button>
        </div>
      )}

      {showTriggers && standingTriggers.length > 0 && (
        <div className={styles.watchDrawer} role="region" aria-label="自动监测与触发规则">
          <EditorialTriggersTable keyPrefix="standing" triggers={standingTriggers} renderText={renderText} />
        </div>
      )}

      {buckets.map((bucket, bucketIndex) => (
        <div key={bucket.group ?? 'flat'} className={bucket.group !== null ? styles.chronicleGroup : undefined}>
          {bucket.group !== null && (
            <h4 className={styles.chronicleGroupTitle}>{bucket.group}</h4>
          )}
          <ol
            className={`${styles.chronicleList} ${bucketIndex === 0 && bucket.group === null && standingTriggers.length === 0 ? styles.chronicleListTopRule : ''}`}
          >
            {bucket.items.map((item) => {
              const isExpanded = expandedId === item.id;
              const headline = item.docLabel ?? item.title;
              const verdictText = item.verdict ?? item.statusLabel ?? defaultStatusLabel(item.status);
              const resolvedSessionId = resolveItemSessionId(item, taskRows);
              const assignDisplay = resolveAuthorDisplay(item, resolvedSessionId);
              const itemTriggers = deriveFallbackTriggers(item);
              const checks = item.checks ?? [];

              return (
                <li
                  key={item.id}
                  className={`${styles.chronicleEntry} ${isExpanded ? styles.chronicleEntryExpanded : ''}`}
                >
                  <div className={styles.chronicleRow}>
                    <button
                      type="button"
                      aria-expanded={isExpanded}
                      className={styles.chronicleTrigger}
                      onClick={() => {
                        setExpandedId(isExpanded ? null : item.id);
                        setActiveGateKey(null);
                      }}
                    >
                      <span className={styles.chronicleDateCol}>
                        <span className={`${styles.chronicleDot} ${markerToneClass(item.status)}`} aria-hidden="true">
                          {item.status === 'running' && <span className={styles.chronicleDotCore} />}
                        </span>
                        <span className={styles.chronicleDate}>{item.date ?? '—'}</span>
                      </span>
                      <span className={styles.chronicleTitle}>{headline}</span>
                    </button>

                    <span className={styles.chronicleMeta}>
                      {assignDisplay !== null && (
                        resolvedSessionId !== null && onOpenWorker !== undefined ? (
                          <button
                            type="button"
                            className={styles.chronicleAssignBtn}
                            onClick={() => { onOpenWorker(resolvedSessionId); }}
                          >
                            {assignDisplay}
                          </button>
                        ) : (
                          <span className={styles.chronicleAssignStatic}>{assignDisplay}</span>
                        )
                      )}
                      <span className={`${styles.chronicleVerdict} ${verdictToneClass(item)}`}>
                        {verdictText}
                      </span>
                    </span>
                  </div>

                  {isExpanded && (
                    <div className={styles.chronicleBody}>
                      {item.triggerAt != null && item.triggerAt.trim() !== '' && (
                        <p className={styles.chronicleTriggerNote}>触发：{item.triggerAt}</p>
                      )}

                      {item.blockedReason != null && item.blockedReason.trim() !== '' && (
                        <div className={styles.blockedNote} role="alert">
                          {text(item.blockedReason)}
                        </div>
                      )}

                      <CheckpointProseBody
                        item={item}
                        renderText={renderText}
                        renderMarkdown={renderMarkdown}
                      />

                      {item.expectations != null && item.expectations.length > 0 && (
                        <CheckpointExpectationsTable
                          keyPrefix={item.id}
                          expectations={item.expectations}
                          renderText={renderText}
                        />
                      )}

                      <EditorialTriggersTable
                        keyPrefix={item.id}
                        triggers={itemTriggers}
                        renderText={renderText}
                      />

                      <EditorialGateStrip
                        itemId={item.id}
                        checks={checks}
                        activeGateKey={activeGateKey}
                        onSelectGateKey={setActiveGateKey}
                        renderText={renderText}
                      />
                    </div>
                  )}
                </li>
              );
            })}
          </ol>
        </div>
      ))}

      {(hiddenCount > 0 || (showAll && !hasGroups && payload.items.length > INITIAL_VISIBLE_COUNT)) && (
        <div className={styles.chronicleFooter}>
          <button
            type="button"
            className={styles.chronicleMore}
            onClick={() => { setShowAll((prev) => !prev); }}
          >
            {hiddenCount > 0 ? `展开更早 ${hiddenCount} 条记录` : '收起更早记录'}
          </button>
        </div>
      )}
    </section>
  );
}
