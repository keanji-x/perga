import type { ReactNode } from 'react';
import type {
  EditorialCheck,
  EditorialExpectation,
  EditorialExpectationStatus,
  EditorialListItem,
  EditorialListStatus,
} from '../../../../../core/domain/report-editorial.ts';
import type { ReportTaskRow } from '../../../../../core/domain/track-task-inventory.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import styles from './editorial.module.css';

export function defaultStatusLabel(status: EditorialListStatus): string {
  switch (status) {
    case 'done': return '已完成';
    case 'running': return '进行中';
    case 'blocked': return '已拦截';
    case 'pending': return '未开始';
  }
}

function bylineStateClass(status: EditorialListStatus): string {
  switch (status) {
    case 'done': return styles.bylineStateDone;
    case 'running': return styles.bylineStateRunning;
    case 'blocked': return styles.bylineStateBlocked;
    case 'pending': return '';
  }
}

export function composeItemMarkdown(item: EditorialListItem): string {
  const parts: string[] = [];
  if (item.summary != null && item.summary.trim() !== '') {
    parts.push(item.summary.trim());
  }
  if (item.bullets != null && item.bullets.length > 0) {
    parts.push(item.bullets.map((bullet) => `- ${bullet}`).join('\n'));
  }
  return parts.join('\n\n');
}

export function resolveItemSessionId(item: EditorialListItem, taskRows?: readonly ReportTaskRow[]): string | null {
  const matchedTask = item.taskKey != null
    ? taskRows?.find((row) => row.key === item.taskKey)
    : undefined;
  return item.sessionId ?? matchedTask?.execution?.workerCardId ?? matchedTask?.workerCardId ?? null;
}

export function resolveAuthorDisplay(item: EditorialListItem, resolvedSessionId: string | null): string | null {
  return item.model ?? item.assign ?? item.workerLabel ?? (resolvedSessionId !== null ? 'Worker' : null);
}

function GateMarker({ status }: Readonly<{ status: EditorialCheck['status'] }>) {
  switch (status) {
    case 'pass':
      return <span className={styles.gateCheckIcon} aria-hidden="true"><Icon name="check" size="sm" /></span>;
    case 'running':
      return (
        <span className={styles.bouncingDots} aria-hidden="true">
          <span className={styles.bouncingDot} />
          <span className={styles.bouncingDot} />
          <span className={styles.bouncingDot} />
        </span>
      );
    case 'fail':
      return <span className={styles.gateFailIcon} aria-hidden="true">×</span>;
    case 'pending':
      return <span className={styles.gatePendingDot} aria-hidden="true" />;
  }
}

export function EditorialByline({
  item,
  taskRows,
  onOpenWorker,
}: Readonly<{
  item: EditorialListItem;
  taskRows?: readonly ReportTaskRow[];
  onOpenWorker?: (sessionId: string) => void;
}>) {
  const resolvedSessionId = resolveItemSessionId(item, taskRows);
  const authorDisplay = resolveAuthorDisplay(item, resolvedSessionId);
  const statusLabel = item.statusLabel ?? defaultStatusLabel(item.status);
  return (
    <div className={styles.byline}>
      {authorDisplay !== null && (
        <>
          {item.assignKind != null && item.model == null && (
            <span className={styles.assignKind}>
              {item.assignKind === 'command' ? 'CLI' : 'Model'}
            </span>
          )}
          {resolvedSessionId !== null && onOpenWorker !== undefined ? (
            <button
              type="button"
              className={styles.bylineAuthor}
              onClick={() => { onOpenWorker(resolvedSessionId); }}
            >
              {authorDisplay}
            </button>
          ) : (
            <span className={styles.bylineAuthorStatic}>{authorDisplay}</span>
          )}
          <span className={styles.bylineSep} aria-hidden="true">·</span>
        </>
      )}
      {item.triggerAt != null && item.triggerAt.trim() !== '' && (
        <>
          <span className={styles.bylineTrigger}>{item.triggerAt}</span>
          <span className={styles.bylineSep} aria-hidden="true">·</span>
        </>
      )}
      <span className={bylineStateClass(item.status)}>{statusLabel}</span>
    </div>
  );
}

export function EditorialItemBody({
  item,
  renderText,
  renderMarkdown,
}: Readonly<{
  item: EditorialListItem;
  renderText?: (text: string) => ReactNode;
  renderMarkdown?: (markdown: string, key: string) => ReactNode;
}>) {
  const text = (value: string) => (renderText !== undefined ? renderText(value) : value);
  const bodyMarkdown = composeItemMarkdown(item);
  if (bodyMarkdown === '') return null;
  return (
    <div className={styles.proseBody}>
      {renderMarkdown !== undefined ? (
        renderMarkdown(bodyMarkdown, `editorial-${item.id}`)
      ) : (
        <>
          {item.summary != null && item.summary.trim() !== '' && (
            <p>{text(item.summary)}</p>
          )}
          {item.bullets != null && item.bullets.length > 0 && (
            <ul className={styles.proseList}>
              {item.bullets.map((bullet, idx) => (
                <li key={`${item.id}-b-${idx}`}>{text(bullet)}</li>
              ))}
            </ul>
          )}
        </>
      )}
    </div>
  );
}

function expectStatusLabel(status: EditorialExpectationStatus | null | undefined): string {
  switch (status) {
    case 'beat': return '超预期';
    case 'inline': return '符合';
    case 'miss': return '未达';
    default: return '待兑现';
  }
}

function expectStatusClass(status: EditorialExpectationStatus | null | undefined): string {
  switch (status) {
    case 'beat': return styles.expectBeat;
    case 'miss': return styles.expectMiss;
    case 'inline': return styles.expectInline;
    default: return styles.expectPending;
  }
}

export function EditorialExpectationsTable({
  keyPrefix,
  expectations,
  renderText,
}: Readonly<{
  keyPrefix: string;
  expectations: readonly EditorialExpectation[];
  renderText?: (text: string) => ReactNode;
}>) {
  if (expectations.length === 0) return null;
  const text = (value: string) => (renderText !== undefined ? renderText(value) : value);
  const hasActual = expectations.some((row) => row.actual != null && row.actual.trim() !== '');

  return (
    <div className={styles.expectWrap}>
      <table className={styles.scheduleTable}>
        <thead>
          <tr>
            <th>指标</th>
            <th>事前预期</th>
            {hasActual && <th>实际兑现</th>}
            <th>判定</th>
          </tr>
        </thead>
        <tbody>
          {expectations.map((row, idx) => (
            <tr key={`${keyPrefix}-ex-${idx}`}>
              <td>{text(row.label)}</td>
              <td>{text(row.expected)}</td>
              {hasActual && <td>{row.actual != null ? text(row.actual) : '—'}</td>}
              <td className={expectStatusClass(row.status)}>
                {expectStatusLabel(row.status)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function EditorialGateStrip({
  itemId,
  checks,
  activeGateKey,
  onSelectGateKey,
  renderText,
}: Readonly<{
  itemId: string;
  checks: readonly EditorialCheck[];
  activeGateKey: string | null;
  onSelectGateKey: (key: string | null) => void;
  renderText?: (text: string) => ReactNode;
}>) {
  if (checks.length === 0) return null;
  const text = (value: string) => (renderText !== undefined ? renderText(value) : value);
  const activeGate = checks.find((_, idx) => `${itemId}-${idx}` === activeGateKey) ?? null;

  return (
    <footer className={styles.gateStrip} aria-label="Gate 验证引用">
      <div className={styles.gateCitationList}>
        <span className={styles.gatePrefix}>Gate</span>
        {checks.map((check, idx) => {
          const key = `${itemId}-${idx}`;
          const hasEvidence = (check.command != null && check.command.trim() !== '')
            || (check.output != null && check.output.trim() !== '');
          const isActive = activeGateKey === key;
          if (!hasEvidence) {
            return (
              <span key={key} className={styles.gateCite}>
                <GateMarker status={check.status} />
                <span>{text(check.label)}</span>
                {check.detail != null && <span className={styles.gateDetailText}>{check.detail}</span>}
              </span>
            );
          }
          return (
            <button
              key={key}
              type="button"
              aria-expanded={isActive}
              className={`${styles.gateCite} ${styles.gateCiteInteractive} ${isActive ? styles.gateCiteActive : ''}`}
              onClick={() => { onSelectGateKey(isActive ? null : key); }}
            >
              <GateMarker status={check.status} />
              <span>{text(check.label)}</span>
              {check.detail != null && <span className={styles.gateDetailText}>{check.detail}</span>}
            </button>
          );
        })}
      </div>

      {activeGate !== null && (
        <div className={styles.gateFootnote} role="region" aria-label={`${activeGate.label} 输出`}>
          {activeGate.command != null && activeGate.command.trim() !== '' && (
            <p className={styles.gateFootnoteCmd}>$ {activeGate.command}</p>
          )}
          {activeGate.output != null && activeGate.output.trim() !== '' && (
            <pre className={styles.gateFootnoteOut}>{activeGate.output}</pre>
          )}
        </div>
      )}
    </footer>
  );
}
