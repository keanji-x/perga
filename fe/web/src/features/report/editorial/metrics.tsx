import type { ReactNode } from 'react';
import type {
  EditorialMetric,
  EditorialMetricDirection,
  EditorialMetricStatus,
} from '../../../../../core/domain/report-editorial.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import styles from './editorial.module.css';

function formatMetricValue(value: number, unit: string | null | undefined): string {
  const formatted = Number.isInteger(value) ? String(value) : value.toFixed(1);
  if (unit == null || unit === '') return formatted;
  return unit === '%' ? `${formatted}%` : `${formatted} ${unit}`;
}

function meetsTarget(
  value: number,
  target: number | null | undefined,
  direction: EditorialMetricDirection | null | undefined,
): boolean {
  if (target == null || direction == null) return true;
  return direction === 'lte' ? value <= target : value >= target;
}

function statusClass(status: EditorialMetricStatus | null | undefined): string {
  switch (status) {
    case 'met': return styles.krStatusMet;
    case 'at_risk': return styles.krStatusRisk;
    case 'on_track': return styles.krStatusTrack;
    default: return styles.krStatusNeutral;
  }
}

export function EditorialMetricsStrip({
  metrics,
  renderText,
  renderMarkdown,
}: Readonly<{
  metrics: readonly EditorialMetric[];
  renderText?: (text: string) => ReactNode;
  renderMarkdown?: (markdown: string, key: string) => ReactNode;
}>) {
  const text = (value: string) => (renderText !== undefined ? renderText(value) : value);

  return (
    <section className={styles.krRoot} aria-label="Key Results 总账">
      <div className={styles.krTableWrap}>
        <table className={styles.krTable}>
          <thead>
            <tr>
              <th className={styles.krTh}>Key Result</th>
              <th className={styles.krTh}>当前</th>
              <th className={styles.krTh}>目标</th>
              <th className={`${styles.krTh} ${styles.krUpdatedCol} ${styles.krLastCol}`}>更新时间</th>
            </tr>
          </thead>
          <tbody>
            {metrics.map((metric, rowIdx) => {
              const latestPoint = metric.points != null && metric.points.length > 0
                ? metric.points[metric.points.length - 1]
                : null;

              const derivedStatus: EditorialMetricStatus | null | undefined = metric.status
                ?? (latestPoint !== null
                  ? (meetsTarget(latestPoint.value, metric.target, metric.direction) ? 'met' : 'at_risk')
                  : null);

              const currentDisplay = metric.current
                ?? (latestPoint !== null ? formatMetricValue(latestPoint.value, metric.unit) : '—');

              const defaultTarget = metric.target != null && metric.direction != null
                ? `${metric.direction === 'lte' ? '≤' : '≥'} ${formatMetricValue(metric.target, metric.unit)}`
                : '—';
              const targetDisplay = metric.targetLabel ?? defaultTarget;
              const updatedDisplay = metric.updatedAt ?? latestPoint?.date ?? '—';

              return (
                <tr key={`${metric.code ?? ''}-${metric.label}-${rowIdx}`}>
                  <td className={styles.krTd}>
                    <span className={styles.krLabelCell}>
                      {metric.code != null && <span className={styles.krCode}>{metric.code}</span>}
                      <span className={styles.krName}>
                        {renderMarkdown !== undefined
                          ? renderMarkdown(metric.label, `kr-lbl-${rowIdx}`)
                          : text(metric.label)}
                      </span>
                      {metric.tag != null && metric.tag.trim() !== '' && (
                        <span className={styles.metricTag}>{metric.tag}</span>
                      )}
                    </span>
                  </td>

                  <td className={`${styles.krTd} ${styles.krCurrentCell}`}>
                    <span className={`${styles.krCurrentValue} ${statusClass(derivedStatus)}`}>
                      {derivedStatus === 'met' && latestPoint === null && (
                        <span className={styles.krCheckIcon} aria-hidden="true">
                          <Icon name="check" size="sm" />
                        </span>
                      )}
                      <span>{currentDisplay}</span>
                    </span>
                  </td>

                  <td className={`${styles.krTd} ${styles.krTargetCell}`}>{targetDisplay}</td>

                  <td className={`${styles.krTd} ${styles.krUpdatedCell} ${styles.krLastCol}`}>
                    {updatedDisplay}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}
