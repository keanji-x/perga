import { useId, useRef } from 'react';

import { useState } from '../../../ui/state/public.ts';

import type { Track } from '../../../../../core/domain/track.js';
import styles from './protocol.module.css';

/** Read-only projection of the server-owned template protocol; no template means no chrome. */
export function TemplateProtocolBar({ track }: { track: Track }) {
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

function inputEntries(value: unknown): [string, unknown][] {
  if (value === null || value === undefined) return [];
  if (typeof value === 'object') return Object.entries(value);
  return [['Input', value]];
}

function Protocol({ track }: { track: Track }) {
  const [open, setOpen] = useState(false);
  const [selected, setSelected] = useState('rules');
  const id = useId();
  const tabRefs = useRef<(HTMLButtonElement | null)[]>([]);
  const header = track.templateHeaderView;
  const rules = header?.rules ?? [];
  const schedules = track.templateSchedules ?? [];
  const inputs = inputEntries(track.templateInputs);
  const tabs = [
    ...(rules.length ? [{ key: 'rules', label: 'Rules' }] : []),
    ...(schedules.length ? [{ key: 'schedules', label: 'Schedules' }] : []),
    ...(inputs.length ? [{ key: 'inputs', label: 'Inputs' }] : []),
  ];
  const active = tabs.some(tab => tab.key === selected) ? selected : tabs[0]?.key;
  return <section className={styles.protocol} aria-label="Template protocol">
    <div className={styles.bar}>
      <div className={styles.badges}>
        <span className={styles.template}>{track.templateTitle || track.templateId}</span>
        {header?.badges?.map((badge, i) => <span key={i} className={`${styles.pill} ${styles[badge.tone ?? 'neutral']}`}>{badge.label}</span>)}
        {schedules.length > 0 && <span className={styles.hint}>{schedules.length} {schedules.length === 1 ? 'schedule' : 'schedules'}</span>}
      </div>
      {tabs.length > 0 && <button type="button" className={styles.toggle} aria-expanded={open} aria-controls={id}
        onClick={() => setOpen(!open)}>Details {open ? '−' : '+'}</button>}
    </div>
    {header?.notice && <p className={`${styles.notice} ${styles[header.notice.tone ?? 'info']}`}>{header.notice.text}</p>}
    {!!header?.metrics?.length && <dl className={styles.metrics}>
      {header.metrics.map((metric, i) => <div key={i} className={`${styles.metric} ${styles[metric.tone ?? 'neutral']}`}>
        <dt>{metric.label}</dt><dd><strong>{metric.value}</strong> {metric.delta && <span>{metric.delta}</span>}</dd>
        {metric.sub && <dd className={styles.hint}>{metric.sub}</dd>}
        {metric.spark && <dd><Sparkline values={metric.spark} label={metric.label} /></dd>}
      </div>)}
    </dl>}
    {open && tabs.length > 0 && <div id={id} className={styles.drawer}>
      <div role="tablist" aria-label="Details" className={styles.tabs}>
        {tabs.map((tab, index) => <button type="button" role="tab" key={tab.key} ref={element => { tabRefs.current[index] = element; }} id={`${id}-${tab.key}`}
          aria-controls={`${id}-panel`} aria-selected={active === tab.key} tabIndex={active === tab.key ? 0 : -1}
          onClick={() => setSelected(tab.key)}
          onKeyDown={event => {
            const next = event.key === 'ArrowRight' ? (index + 1) % tabs.length
              : event.key === 'ArrowLeft' ? (index + tabs.length - 1) % tabs.length
                : event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : null;
            if (next === null) return;
            event.preventDefault();
            setSelected(tabs[next].key);
            tabRefs.current[next]?.focus();
          }}>{tab.label}</button>)}
      </div>
      <div role="tabpanel" id={`${id}-panel`} aria-labelledby={`${id}-${active}`} tabIndex={0}>
        {active === 'rules' && <dl>{rules.map((rule, i) => <div key={i}><dt>{rule.label}</dt><dd>{rule.text}</dd></div>)}</dl>}
        {active === 'schedules' && <dl>{schedules.map((schedule, i) => <div key={i}><dt>{schedule.id}</dt><dd><code>{schedule.cron}</code> · {schedule.timezone}</dd></div>)}</dl>}
        {active === 'inputs' && <dl>{inputs.map(([name, value]) => <div key={name}><dt>{name}</dt><dd>{typeof value === 'string' ? value : JSON.stringify(value)}</dd></div>)}</dl>}
      </div>
    </div>}
  </section>;
}
