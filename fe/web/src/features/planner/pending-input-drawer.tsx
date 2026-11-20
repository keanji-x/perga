import type { ReactNode } from 'react';
import { Button } from '@astryxdesign/core/Button';
import { ChatComposerDrawer } from '@astryxdesign/core/Chat';
import { useState } from '../../ui/state/public.ts';
import styles from './asks.module.css';

/** Queue selection and settlement are shared; each input form owns its draft and write. */
export function PendingInputDrawer<T>({ items, idOf, titleOf, label, noun, queueLabel, countOf, children }: Readonly<{
  items: readonly T[];
  idOf: (item: T) => string;
  titleOf: (item: T) => string;
  label: string;
  noun: string;
  queueLabel: string;
  countOf?: (item: T) => number;
  children: (item: T, onBusy: (busy: boolean) => void, onSettled: () => void) => ReactNode;
}>) {
  const [settled, setSettled] = useState<ReadonlySet<string>>(() => new Set());
  const [selected, setSelected] = useState<string | null>(null);
  const [queueOpen, setQueueOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const open = items.filter(item => !settled.has(idOf(item)));
  const current = open.find(item => idOf(item) === selected) ?? open[0];
  if (current === undefined) return null;
  const currentId = idOf(current);
  return <ChatComposerDrawer count={countOf?.(current) ?? open.length} label={label} className={styles.drawer}>
    <div className={styles.asks} data-nc-asks="">
      {children(current, setBusy, () => {
        setSettled(previous => new Set(previous).add(currentId));
        setSelected(null);
        setQueueOpen(false);
      })}
      {open.length > 1 && <div className={styles.queue}>
        <Button label={`${open.length - 1} more ${noun}${open.length === 2 ? '' : 's'}`}
          variant="secondary" size="sm" className={styles.queueToggle} isDisabled={busy}
          aria-expanded={queueOpen} onClick={() => setQueueOpen(value => !value)} />
        {queueOpen && <div className={styles.queueList} role="group" aria-label={queueLabel}>
          {open.filter(item => idOf(item) !== currentId).map(item => <Button key={idOf(item)}
            label={titleOf(item)} variant="secondary" size="sm" className={styles.queueItem}
            isDisabled={busy} onClick={() => { setSelected(idOf(item)); setQueueOpen(false); }} />)}
        </div>}
      </div>}
    </div>
  </ChatComposerDrawer>;
}
