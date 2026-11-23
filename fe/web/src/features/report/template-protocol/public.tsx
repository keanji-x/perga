import { useId } from 'react';

import { Icon } from '../../../ui/icon/public.tsx';
import { SpringRotation } from '../../../ui/motion/rotation.tsx';
import { useState } from '../../../ui/state/public.ts';

import type { Track } from '../../../../../core/domain/track.js';
import styles from './protocol.module.css';

export type TemplateProtocolProps = Readonly<{
  track: Track;
}>;

/** Renders the active template's quiet identity and optional read-only header view. */
export function TemplateProtocolBar({ track }: TemplateProtocolProps) {
  return track.templateId ? <Protocol key={track.id} track={track} /> : null;
}

function Sparkline({ values, label }: { values: readonly number[]; label: string }) {
  if (values.length < 2) return null;
  const low = values.reduce((min, value) => Math.min(min, value), Infinity);
  const high = values.reduce((max, value) => Math.max(max, value), -Infinity);
  const points = values.map((value, i) => `${i / (values.length - 1) * 100},${high === low ? 15 : 28 - (value - low) / (high - low) * 26}`).join(' ');
  return <svg className={styles.spark} viewBox="0 0 100 30" role="img" aria-label={`${label} trend`}>
    <polyline points={points} fill="none" stroke="currentColor" strokeWidth="2" />
  </svg>;
}

function Protocol({ track }: TemplateProtocolProps) {
  const [open, setOpen] = useState(false);
  const id = useId();
  const header = track.templateHeaderView;
  const rules = header?.rules ?? [];
  return <section className={styles.protocol} aria-label="Template protocol">
    <div className={styles.bar}>
      <div className={styles.badges}>
        <span className={styles.template}>{track.templateTitle || track.templateId}</span>
        {header?.badges?.map((badge, i) => <span key={i} className={`${styles.pill} ${styles[badge.tone ?? 'neutral']}`}>{badge.label}</span>)}
      </div>
      {rules.length > 0 && (
        <button type="button" className={styles.toggle} aria-expanded={open} aria-controls={id}
          onClick={() => setOpen(!open)}>
          <span>Rules</span>
          <SpringRotation className={styles.toggleChevron} angle={open ? 90 : 0}>
            <Icon name="chevron-right" size="sm" />
          </SpringRotation>
        </button>
      )}
    </div>
    {header?.notice && <p className={`${styles.notice} ${styles[header.notice.tone ?? 'info']}`}>{header.notice.text}</p>}
    {!!header?.metrics?.length && <dl className={styles.metrics}>
      {header.metrics.map((metric, i) => <div key={i} className={`${styles.metric} ${styles[metric.tone ?? 'neutral']}`}>
        <dt>{metric.label}</dt><dd><strong>{metric.value}</strong> {metric.delta && <span>{metric.delta}</span>}</dd>
        {metric.sub && <dd className={styles.hint}>{metric.sub}</dd>}
        {metric.spark && <dd><Sparkline values={metric.spark} label={metric.label} /></dd>}
      </div>)}
    </dl>}
    {open && rules.length > 0 && (
      <div id={id} className={styles.drawer}>
        <dl className={styles.rulesList}>
          {rules.map((rule, i) => <div key={i}><dt>{rule.label}</dt><dd>{rule.text}</dd></div>)}
        </dl>
      </div>
    )}
  </section>;
}
