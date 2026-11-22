import { useId, useRef } from 'react';

import { useState } from '../../../ui/state/public.ts';

import type { Track } from '../../../../../core/domain/track.js';
import { InputsPanel } from './inputs.tsx';
import styles from './protocol.module.css';

export type TemplateProtocolProps = Readonly<{
  track: Track;
  inputsSchema?: unknown;
  loading?: boolean;
  error?: string;
  onSaveInputs?: (patch: Readonly<Record<string, unknown>>) => Promise<void>;
}>;

/** The app owns reads and writes; this feature renders the protocol and captures edits. */
export function TemplateProtocolBar(props: TemplateProtocolProps) {
  return props.track.templateId ? <Protocol key={props.track.id} {...props} /> : null;
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

function Protocol({ track, inputsSchema, loading, error, onSaveInputs }: TemplateProtocolProps) {
  const [open, setOpen] = useState(false);
  const [selected, setSelected] = useState('rules');
  const id = useId();
  const tabRefs = useRef<(HTMLButtonElement | null)[]>([]);
  const header = track.templateHeaderView;
  const rules = header?.rules ?? [];
  const tabs = [
    ...(rules.length ? [{ key: 'rules', label: 'Rules' }] : []),
    { key: 'inputs', label: 'Inputs' },
  ];
  const active = tabs.some(tab => tab.key === selected) ? selected : tabs[0]?.key;
  return <section className={styles.protocol} aria-label="Template protocol">
    <div className={styles.bar}>
      <div className={styles.badges}>
        <span className={styles.template}>{track.templateTitle || track.templateId}</span>
        {header?.badges?.map((badge, i) => <span key={i} className={`${styles.pill} ${styles[badge.tone ?? 'neutral']}`}>{badge.label}</span>)}
      </div>
      <button type="button" className={styles.toggle} aria-expanded={open} aria-controls={id}
        onClick={() => setOpen(!open)}>Details {open ? '−' : '+'}</button>
    </div>
    {header?.notice && <p className={`${styles.notice} ${styles[header.notice.tone ?? 'info']}`}>{header.notice.text}</p>}
    {!!header?.metrics?.length && <dl className={styles.metrics}>
      {header.metrics.map((metric, i) => <div key={i} className={`${styles.metric} ${styles[metric.tone ?? 'neutral']}`}>
        <dt>{metric.label}</dt><dd><strong>{metric.value}</strong> {metric.delta && <span>{metric.delta}</span>}</dd>
        {metric.sub && <dd className={styles.hint}>{metric.sub}</dd>}
        {metric.spark && <dd><Sparkline values={metric.spark} label={metric.label} /></dd>}
      </div>)}
    </dl>}
    {open && <div id={id} className={styles.drawer}>
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
        {active === 'inputs' && (
          loading ? <p role="status">Loading inputs…</p>
            : error ? <p role="alert">{error}</p>
              : <InputsPanel schema={inputsSchema} inputs={track.templateInputs} onSave={onSaveInputs} />
        )}
      </div>
    </div>}
  </section>;
}
