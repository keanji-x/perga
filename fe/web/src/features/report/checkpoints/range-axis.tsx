import type { CheckpointRange } from '../../../../../core/domain/report-checkpoints.ts';
import { useState } from '../../../ui/state/public.ts';
import styles from './checkpoints.module.css';

function formatPointValue(value: number, unit: string | null | undefined, display?: string | null): string {
  if (display != null && display.trim() !== '') return display;
  return `${unit ?? ''}${value}`;
}

export function CheckpointRangeAxis({ range }: Readonly<{ range: CheckpointRange }>) {
  const [hoveredKey, setHoveredKey] = useState<string | null>(null);
  const [pinnedKey, setPinnedKey] = useState<string | null>(null);

  const sortedPoints = [...range.points].sort((a, b) => a.value - b.value);
  const allValues = sortedPoints.map((p) => p.value);
  if (range.current != null) allValues.push(range.current.value);

  const rawMin = Math.min(...allValues);
  const rawMax = Math.max(...allValues);
  const rawSpan = rawMax - rawMin || 1;
  // 10% breathing room at both ends so extreme price labels stay well inside the reading column
  const pad = rawSpan * 0.1;
  const domainMin = rawMin - pad;
  const domainSpan = rawSpan + pad * 2;
  const pct = (value: number) => `${((value - domainMin) / domainSpan) * 100}%`;

  const lowPct = pct(sortedPoints[0].value);
  const spanWidthPct = `${((sortedPoints[sortedPoints.length - 1].value - sortedPoints[0].value) / domainSpan) * 100}%`;

  const activeKey = hoveredKey ?? pinnedKey;

  let activeLabel: string | null = null;
  let activeNote: string | null = null;
  if (activeKey === 'current' && range.current != null) {
    activeLabel = formatPointValue(range.current.value, range.unit, range.current.display);
    activeNote = range.current.note ?? (range.current.label ?? '当前参考价');
  } else if (activeKey !== null && activeKey.startsWith('pt-')) {
    const idx = Number(activeKey.slice(3));
    const pt = sortedPoints[idx];
    if (pt !== undefined) {
      activeLabel = formatPointValue(pt.value, range.unit, pt.display);
      activeNote = pt.note;
    }
  }

  return (
    <figure className={styles.rangeRoot} aria-label="目标价区间坐标轴">
      <div
        className={styles.rangeTrackArea}
        onMouseLeave={() => { setHoveredKey(null); }}
      >
        <div className={styles.rangeBaseline} aria-hidden="true" />
        <div
          className={styles.rangeSpan}
          aria-hidden="true"
          style={{ insetInlineStart: lowPct, inlineSize: spanWidthPct }}
        />

        {/* Current price sits ABOVE the axis so it never collides with target prices below */}
        {range.current != null && (
          <button
            type="button"
            className={`${styles.rangeCurrentBtn} ${activeKey === 'current' ? styles.rangeNodeActive : ''}`}
            style={{ insetInlineStart: pct(range.current.value) }}
            aria-label={`${range.current.label ?? '现价'} ${formatPointValue(range.current.value, range.unit, range.current.display)}`}
            onMouseEnter={() => { setHoveredKey('current'); }}
            onFocus={() => { setHoveredKey('current'); }}
            onBlur={() => { setHoveredKey((prev) => (prev === 'current' ? null : prev)); }}
            onClick={() => { setPinnedKey((prev) => (prev === 'current' ? null : 'current')); }}
          >
            <span className={styles.rangeCurrentLabel}>
              {range.current.label ?? '现价'} {formatPointValue(range.current.value, range.unit, range.current.display)}
            </span>
            <span className={styles.rangeCurrentStem} aria-hidden="true" />
            <span className={styles.rangeCurrentDot} aria-hidden="true" />
          </button>
        )}

        {/* Case target dots sit cleanly ON the axis with price figures BELOW (position encodes low -> high) */}
        {sortedPoints.map((pt, idx) => {
          const key = `pt-${idx}`;
          const isActive = activeKey === key;
          const display = formatPointValue(pt.value, range.unit, pt.display);
          return (
            <button
              key={key}
              type="button"
              className={`${styles.rangePointBtn} ${pt.emphasis === true ? styles.rangePointEmphasis : ''} ${isActive ? styles.rangeNodeActive : ''}`}
              style={{ insetInlineStart: pct(pt.value) }}
              aria-label={`${display}：${pt.note}`}
              onMouseEnter={() => { setHoveredKey(key); }}
              onFocus={() => { setHoveredKey(key); }}
              onBlur={() => { setHoveredKey((prev) => (prev === key ? null : prev)); }}
              onClick={() => { setPinnedKey((prev) => (prev === key ? null : key)); }}
            >
              <span className={styles.rangePointDot} aria-hidden="true" />
              <span className={styles.rangePointValue}>{display}</span>
            </button>
          );
        })}
      </div>

      {/* Reserved single-line caption strip: zero layout shift on hover */}
      <figcaption className={styles.rangeCaption} aria-live="polite">
        {activeNote !== null ? (
          <>
            <span className={styles.rangeCaptionPrice}>{activeLabel}</span>
            <span className={styles.rangeCaptionSep} aria-hidden="true">·</span>
            <span className={styles.rangeCaptionText}>{activeNote}</span>
          </>
        ) : (
          <span className={styles.rangeCaptionHint}>悬停价位点查看核心假设</span>
        )}
      </figcaption>
    </figure>
  );
}
