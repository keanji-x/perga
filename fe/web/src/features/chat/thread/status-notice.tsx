import { Fragment, useLayoutEffect, useMemo, useRef, type ReactNode } from 'react';
import { Divider } from '@astryxdesign/core/Divider';
import { Collapsible } from '@astryxdesign/core/Collapsible';
import { MessageActions } from './message-actions.tsx';
import { formatTurnTimestamp, type ConversationMetaClock } from '../../../../../core/domain/conversation-meta.ts';
import { useState } from '../../../ui/state/public.ts';
import { restoreDrawerFocus } from '../../../ui/drawer/restore-focus.ts';
import styles from './meta.module.css';

export type CopyResponseAction = Readonly<{ id: string; text: string; run: () => Promise<void> }>;
/**
 * `id` names the response it acts on; `run` settles once the action is handed on, and any failure is the caller's to show:
 * Regenerate is a send, whose failure the outbox reads through its table and shows above the composer.
 */
export type ResponseAction = Readonly<{ id: string; run: () => Promise<void> }>;

type ActionView = { readonly key: string; active: boolean };
type ActionResult = Readonly<{ view: ActionView; kind: 'pending' | 'done' | 'failed' }>;

/**
 * One run at a time per view; a completion speaks only while its run is still the latest one. A failure is only a
 * state: its rejection's text is never shown.
 */
export function useFencedAction(key: string | null) {
  const view = useMemo<ActionView | null>(() => key === null ? null : { key, active: false }, [key]);
  const [result, setResult] = useState<ActionResult | null>(null);
  const perform = async (run: () => Promise<void>) => {
    if (view === null || view.active) return;
    view.active = true;
    const started: ActionResult = { view, kind: 'pending' };
    const settle = (next: ActionResult) => setResult((current) => current === started ? next : current);
    setResult(started);
    try {
      await run();
      settle({ view, kind: 'done' });
    } catch {
      settle({ view, kind: 'failed' });
    } finally { view.active = false; }
  };
  return { feedback: result?.view === view ? result : null, perform };
}


function elapsedText(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  return seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

/** One native metadata row; clocks are evidence, never inferred from transcript gaps. */
export function ThreadStatusNotice({ historical = false, ariaLabel, messageActions = 'all', heading, details, leading, children, clock, sending = false, tone = 'neutral', outcome, copyAction = null, regenerateAction = null, detailsExpanded, onDetailsExpandedChange }: {
  detailsExpanded?: boolean;
  onDetailsExpandedChange?: (expanded: boolean) => void;
  historical?: boolean;
  ariaLabel?: string;
  messageActions?: 'all' | 'copy-only';
  heading: ReactNode;
  details?: readonly string[];
  leading?: ReactNode;
  children?: ReactNode;
  clock: ConversationMetaClock;
  sending?: boolean;
  tone?: 'neutral' | 'warning' | 'error';
  outcome?: 'completed' | 'interrupted' | 'failed';
  copyAction?: CopyResponseAction | null;
  regenerateAction?: ResponseAction | null;
}) {
  const stateRef = useRef<HTMLDivElement | null>(null);
  const lastFocused = useRef<HTMLElement | null>(null);
  const hasDetails = children !== undefined;
  useLayoutEffect(() => {
    const previous = lastFocused.current;
    if (previous !== null && !previous.isConnected && document.activeElement === document.body) {
      const target = stateRef.current;
      if (target !== null) restoreDrawerFocus(target);
    }
  }, [hasDetails]);
  const copy = useFencedAction(copyAction === null ? null : JSON.stringify([copyAction.id, copyAction.text]));
  const feedback = copy.feedback;
  const regenerate = useFencedAction(regenerateAction?.id ?? null);
  const timestamp = clock.timestamp;
  const formattedTime = timestamp === null ? null : formatTurnTimestamp(timestamp.atMs);
  const timePrefix = timestamp?.kind === 'paused' ? 'Paused' : 'Ended';
  const title = <span className={styles.summary}><span className={styles.heading}>{leading ? <span className={styles.leading}>{leading}</span> : null}<span className={styles.label}>{heading}</span></span>
    {clock.elapsedMs !== null && <span className={styles.clock} data-nc-meta-duration="">· {elapsedText(clock.elapsedMs)}</span>}
    {formattedTime !== null && timestamp !== null && <span className={styles.timestamp} data-nc-meta-timestamp="">
      <span className={styles.detailSep} aria-hidden="true">·</span>{' '}
      <time dateTime={new Date(timestamp.atMs).toISOString()} aria-label={`${timePrefix} at ${formattedTime}`}>{timePrefix} {formattedTime}</time>
    </span>}
    {details?.map((detail) => (
      <Fragment key={detail}>
        <span className={styles.detailSep} aria-hidden="true">·</span>
        <span className={styles.detailItem}>{detail}</span>
      </Fragment>
    ))}
  </span>;
  return <div className={`${styles.notice} ${tone === 'warning' ? styles.warning : tone === 'error' ? styles.error : ''}`}
    role="status" aria-label={ariaLabel ?? (historical ? 'Historical response status' : 'Current response status')} data-nc-current-meta={historical ? undefined : ""} data-nc-turn={outcome === undefined ? undefined : 'outcome'}
    data-nc-turn-outcome={outcome}>
    <Divider />
    <div className={styles.row}>
      <div ref={stateRef} className={styles.state} data-nc-meta-state="" tabIndex={-1}
        onFocus={(event) => { lastFocused.current = event.target; }} onBlur={(event) => {
          if (!event.currentTarget.contains(event.relatedTarget)) {
            if (event.relatedTarget !== null || event.target.isConnected) lastFocused.current = null;
          }
        }}>
        {children === undefined ? <div className={styles.normal}>{title}</div>
          : <Collapsible defaultIsOpen={false} isOpen={detailsExpanded} onOpenChange={onDetailsExpandedChange} chevronPosition="start" trigger={title}>{children}</Collapsible>}
        {sending && <div className={styles.label}>Sending…</div>}
      </div>
      {!historical && <MessageActions actions={[
        { id: 'copy', kind: feedback?.kind === 'done' ? 'copied' : 'copy',
          label: copyAction === null ? 'Copy reply (unavailable)' : feedback === null || feedback.kind === 'pending' ? 'Copy reply'
            : feedback.kind === 'done' ? 'Copied reply' : 'Copy failed',
          disabled: copyAction === null || feedback?.kind === 'pending',
          run: () => { if (copyAction !== null) void copy.perform(copyAction.run); } },
        ...(messageActions === 'copy-only' ? [] : [{ id: 'regenerate', kind: 'regenerate' as const, label: regenerateAction === null ? 'Regenerate (unavailable)' : 'Regenerate',
          tooltip: 'Resend the original message in this conversation while keeping history.',
          disabled: regenerateAction === null || regenerate.feedback?.kind === 'pending',
          run: () => { if (regenerateAction !== null) void regenerate.perform(regenerateAction.run); } }]),
      ]} />}
    </div>
  </div>;
}
