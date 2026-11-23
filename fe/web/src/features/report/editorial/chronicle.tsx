import type { ReactNode } from 'react';
import type {
  EditorialListItem,
  EditorialListStatus,
} from '../../../../../core/domain/report-editorial.ts';
import type { ReportTaskRow } from '../../../../../core/domain/track-task-inventory.ts';
import { useState } from '../../../ui/state/public.ts';
import { EditorialTable } from './table.tsx';
import {
  EditorialGateStrip,
  EditorialItemBody,
  defaultStatusLabel,
  resolveItemSessionId,
} from './primitives.tsx';
import styles from './chronicle.module.css';
import editorialStyles from './editorial.module.css';

function markerToneClass(status: EditorialListStatus): string {
  switch (status) {
    case 'done': return styles.markerDone;
    case 'running': return styles.markerRunning;
    case 'blocked': return styles.markerBlocked;
    case 'pending': return styles.markerPending;
  }
}

const INITIAL_VISIBLE_COUNT = 6;

function initialExpandedId(items: readonly EditorialListItem[]): string | null {
  const active = items.find((item) => item.status === 'running' || item.status === 'blocked');
  return active?.id ?? items[0]?.id ?? null;
}

type GroupedBucket = Readonly<{
  group: string | null;
  items: readonly EditorialListItem[];
}>;

function groupVisibleItems(items: readonly EditorialListItem[], groups: readonly string[]): readonly GroupedBucket[] {
  const hasGroups = items.some((item) => item.group != null && item.group.trim() !== '');
  if (!hasGroups) {
    return [{ group: null, items }];
  }
  const byName = new Map<string, EditorialListItem[]>(groups.map((name) => [name.trim(), []]));
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

export function EditorialChronicle({
  payload,
  taskRows,
  renderText,
  renderMarkdown,
  onOpenWorker,
}: Readonly<{
  payload: Readonly<{
    items: readonly EditorialListItem[];
    title?: string | null;
    summary?: string | null;
    groups?: readonly string[] | null;
  }>;
  taskRows?: readonly ReportTaskRow[];
  renderText?: (text: string) => ReactNode;
  renderMarkdown?: (markdown: string, key: string) => ReactNode;
  onOpenWorker?: (sessionId: string) => void;
}>) {
  const [expandedId, setExpandedId] = useState<string | null>(() => initialExpandedId(payload.items));
  const [showAll, setShowAll] = useState(false);
  const [activeGateKey, setActiveGateKey] = useState<string | null>(null);

  const text = (value: string) => (renderText !== undefined ? renderText(value) : value);

  const hasGroups = payload.items.some((item) => item.group != null && item.group.trim() !== '');
  const visibleItems = (showAll || hasGroups)
    ? payload.items
    : payload.items.slice(0, INITIAL_VISIBLE_COUNT);
  const hiddenCount = payload.items.length - visibleItems.length;
  const buckets = groupVisibleItems(visibleItems, payload.groups ?? []);

  return (
    <section className={styles.root} aria-label={payload.title ?? '记录列表'}>
      {payload.summary != null && payload.summary !== '' && (
        renderMarkdown?.(payload.summary, 'chronicle-summary') ?? <p>{text(payload.summary)}</p>
      )}
      {buckets.map((bucket, bucketIndex) => (
        <div key={bucket.group ?? 'flat'} className={bucket.group !== null ? styles.chronicleGroup : undefined}>
          {bucket.group !== null && (
            <h4 className={styles.chronicleGroupTitle}>{bucket.group}</h4>
          )}
          <ol
            className={`${styles.chronicleList} ${bucketIndex === 0 && bucket.group === null ? styles.chronicleListTopRule : ''}`}
          >
            {bucket.items.map((item) => {
              const isExpanded = expandedId === item.id;
              const badge = item.badge ?? defaultStatusLabel(item.status);
              const resolvedSessionId = resolveItemSessionId(item, taskRows);
              const canOpenWorker = resolvedSessionId !== null && onOpenWorker !== undefined;
              const meta = item.meta ?? (canOpenWorker ? 'Worker' : null);
              const tone = item.tone ?? (item.status === 'running' ? 'accent' : item.status === 'blocked' ? 'bad' : 'neutral');
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
                        {item.date != null && <span className={styles.chronicleDate}>{item.date}</span>}
                      </span>
                      <span>
                        <span className={styles.chronicleTitle}>{item.title}</span>
                        {item.subtitle != null && <span className={styles.chronicleSubtitle}>{item.subtitle}</span>}
                      </span>
                    </button>

                    <span className={styles.chronicleMeta}>
                      {meta !== null && (
                        canOpenWorker ? (
                          <button
                            type="button"
                            className={styles.chronicleMetaButton}
                            onClick={() => { onOpenWorker(resolvedSessionId); }}
                          >
                            {meta}
                          </button>
                        ) : (
                          <span className={styles.chronicleMetaLabel}>{meta}</span>
                        )
                      )}
                      <span className={`${styles.chronicleBadge} ${editorialStyles.tone}`} data-nc-tone={tone}>
                        {badge}
                      </span>
                    </span>
                  </div>

                  {isExpanded && (
                    <div className={styles.chronicleBody}>
                      <EditorialItemBody
                        item={item}
                        renderText={renderText}
                        renderMarkdown={renderMarkdown}
                      />

                      {item.table != null && (
                        <EditorialTable
                          payload={item.table}
                          title={item.title}
                          renderText={renderText}
                          renderMarkdown={renderMarkdown}
                        />
                      )}

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
