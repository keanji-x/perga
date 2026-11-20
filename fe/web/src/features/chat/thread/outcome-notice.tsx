import type { CurrentConversationOutcome } from '../../../../../core/domain/conversation-display.ts';
import type { ConversationTurnOutcome } from '../../../../../core/domain/conversation.ts';
import { ThreadStatusNotice, type CopyResponseAction, type ResponseAction } from './status-notice.tsx';
import { useRunningElapsedMs } from './running-clock.ts';
import styles from './thread.module.css';

/** One plain sentence for the `codexErrorInfo` values a reader can act on; every other code is shown as the token codex sent. */
const FAILURE_HINTS: Readonly<Record<string, string>> = Object.freeze({
  contextWindowExceeded: 'The conversation no longer fits in the model’s context window.',
  usageLimitExceeded: 'The usage limit for this account has been reached.',
  rateLimitExceeded: 'Requests are being rate-limited; try again in a moment.',
  serverOverloaded: 'The model provider is overloaded; try again in a moment.',
});

/** The current Turn line reads only the server's lastTurn, independently of session control state. */
export function CurrentStatusNotice({ lastTurn, messageActions = 'all', copyAction, regenerateAction, detailsExpanded, onDetailsExpandedChange, leading }: {
  leading?: import('react').ReactNode;
  lastTurn: import('../../../../../core/api/perga-v1.ts').SessionTurnDto | null;
  messageActions?: 'all' | 'copy-only';
  copyAction: CopyResponseAction | null;
  regenerateAction: ResponseAction | null;
  detailsExpanded?: boolean;
  onDetailsExpandedChange?: (expanded: boolean) => void;
}) {
  const elapsed = useRunningElapsedMs(lastTurn?.status === 'running' ? lastTurn.startedAtMs : null);
  if (lastTurn === null) return null;
  const status = lastTurn.status;
  const heading = status === 'running' ? '本轮生成中' : status === 'completed' ? '完成'
    : status === 'interrupted' ? '本轮已中断' : status === 'failed' ? '本轮执行失败' : '本轮结果未确认';
  const terminal = status === 'completed' || status === 'interrupted' || status === 'failed' ? status : undefined;
  return <ThreadStatusNotice messageActions={messageActions} detailsExpanded={detailsExpanded} onDetailsExpandedChange={onDetailsExpandedChange}
    heading={heading} leading={status === 'running' ? leading : undefined} tone={status === 'failed' ? 'error' : status === 'unknown' || status === 'interrupted' ? 'warning' : 'neutral'}
    clock={{ elapsedMs: status === 'running' ? elapsed : lastTurn.startedAtMs != null && lastTurn.completedAtMs != null
      ? Math.max(0, lastTurn.completedAtMs - lastTurn.startedAtMs) : null, timestamp: null }}
    outcome={terminal} copyAction={copyAction} regenerateAction={terminal === undefined ? null : regenerateAction}>
    {lastTurn.errorText ? <p className={styles.outcomeReason}>{lastTurn.errorText}</p> : undefined}
  </ThreadStatusNotice>;
}

/** Imported outcomes remain at their historical position, with no execution actions. */
export function HistoricalOutcomeNotice({ outcome }: { outcome: ConversationTurnOutcome }) {
  return <ThreadStatusNotice historical heading={`Imported history · ${outcomeHeading(outcome.status)}`}
    outcome={outcome.status} clock={{elapsedMs:outcome.elapsedMs,timestamp:{kind:'finished',atMs:outcome.atMs}}}>
    {outcome.status === 'completed' ? undefined : <OutcomeDetails outcome={outcome} canContinue={false} />}
  </ThreadStatusNotice>;
}
function outcomeHeading(status: ConversationTurnOutcome['status']): string {
  return status === 'completed' ? 'Completed' : status === 'interrupted' ? 'Interrupted' : 'Failed';
}

/** Recorded diagnostics stay readable even when a run query cannot confirm current execution. */
function OutcomeDetails({ outcome, canContinue }: { outcome: CurrentConversationOutcome; canContinue: boolean }) {
  if (outcome.status === 'completed') return null;
  const hasReason = outcome.text !== undefined && outcome.text.trim() !== '';
  const hint = outcome.status === 'failed' ? outcomeHintText(outcome.code, outcome.rawStatus) : null;
  return <>
    {hasReason && <p className={styles.outcomeReason} data-nc-turn-outcome-message="" title={outcome.message}>{outcome.text}</p>}
    {hint !== null && <p className={styles.outcomeReason} data-nc-turn-outcome-hint="">{hint}</p>}
    {!hasReason && hint === null && <p className={styles.outcomeReason} data-nc-turn-outcome-fallback="">
      {outcome.status === 'failed' ? 'No failure details are available.' : 'No interruption details are available.'}
    </p>}
    {canContinue && <p className={styles.outcomeGuidance} data-nc-interruption-guidance="">Send a message to continue.</p>}
  </>;
}

function outcomeHintText(code: string | undefined, rawStatus: string | undefined): string | null {
  const hint = code === undefined || code.trim() === '' ? null : (FAILURE_HINTS[code] ?? code);
  return hint ?? (rawStatus === undefined ? null : `Ended with status “${rawStatus}”`);
}
