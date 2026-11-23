import { formatMessage, MESSAGES } from '../../../../../core/domain/messages.ts';
import type { SessionItemsPageDto, SessionTurnDto } from '../../../../../core/api/perga-v1.ts';
import type { CurrentConversationOutcome } from '../../../../../core/domain/conversation-display.ts';
import type { ConversationTurnOutcome } from '../../../../../core/domain/conversation.ts';
import { ThreadStatusNotice, type CopyResponseAction, type ResponseAction } from './status-notice.tsx';
import { useRunningElapsedMs } from './running-clock.ts';
import styles from './thread.module.css';

/** One plain sentence for provider error codes a reader can act on; every other code is shown as received. */
const FAILURE_HINTS: Readonly<Record<string, string>> = Object.freeze({
  contextWindowExceeded: 'The conversation no longer fits in the model’s context window.',
  usageLimitExceeded: 'The usage limit for this account has been reached.',
  rateLimitExceeded: 'Requests are being rate-limited; try again in a moment.',
  serverOverloaded: 'The model provider is overloaded; try again in a moment.',
});

type RuntimeTasksProp = SessionItemsPageDto['runtimeTasks']
  | readonly NonNullable<SessionItemsPageDto['runtimeTasks']>[number][];

function runtimePhaseLabel(runtimeStatus: SessionItemsPageDto['runtimeStatus'] | undefined): string | null {
  if (runtimeStatus?.kind !== 'running') return null;
  switch (runtimeStatus.phase) {
    case 'thinking': return MESSAGES.chat.thinking;
    case 'responding': return MESSAGES.chat.responding;
    case 'waiting_model': return MESSAGES.chat.waitingModel;
    case 'compacting': return MESSAGES.chat.statusCompacting;
    case 'tool': return formatMessage(MESSAGES.chat.toolPrefix, { name: runtimeStatus.toolName ?? 'running' });
    default: return null;
  }
}

export function activeBackgroundTaskCount(runtimeTasks: RuntimeTasksProp | undefined): number {
  return runtimeTasks?.filter(task => !task.ambient
    && (task.status === 'running' || task.status === 'pending' || task.status === 'paused')).length ?? 0;
}

/** The current Turn line combines the server's lastTurn with live runtime phase and background task liveness. */
export function CurrentStatusNotice({ lastTurn, runtimeStatus, runtimeTasks, messageActions = 'all', copyAction, regenerateAction, detailsExpanded, onDetailsExpandedChange, leading }: {
  leading?: import('react').ReactNode;
  lastTurn: SessionTurnDto | null;
  runtimeStatus?: SessionItemsPageDto['runtimeStatus'];
  runtimeTasks?: RuntimeTasksProp;
  messageActions?: 'all' | 'copy-only';
  copyAction: CopyResponseAction | null;
  regenerateAction: ResponseAction | null;
  detailsExpanded?: boolean;
  onDetailsExpandedChange?: (expanded: boolean) => void;
}) {
  const elapsed = useRunningElapsedMs(lastTurn?.status === 'running' ? lastTurn.startedAtMs : null);
  const phaseLabel = runtimePhaseLabel(runtimeStatus);
  const backgroundTasks = activeBackgroundTaskCount(runtimeTasks);
  const hasIdleActivity = runtimeStatus?.kind === 'idle' && runtimeStatus.idleActivity != null;

  if (lastTurn === null) {
    if (phaseLabel === null && backgroundTasks === 0 && !hasIdleActivity) return null;
    const heading = phaseLabel ?? (backgroundTasks > 0
      ? formatMessage(MESSAGES.chat.backgroundTasks, { count: backgroundTasks })
      : MESSAGES.chat.recentBackgroundActivity);
    const details = phaseLabel !== null && backgroundTasks > 0
      ? [formatMessage(MESSAGES.chat.backgroundTasks, { count: backgroundTasks })]
      : undefined;
    const active = phaseLabel !== null || backgroundTasks > 0;
    return <ThreadStatusNotice messageActions={messageActions}
      heading={heading} details={details} leading={active ? leading : undefined}
      clock={{ elapsedMs: null, timestamp: null }}
      copyAction={null} regenerateAction={null} />;
  }

  const status = lastTurn.status;
  const heading = status === 'running' ? (phaseLabel ?? MESSAGES.chat.turnRunning) : status === 'completed' ? MESSAGES.chat.turnDone
    : status === 'interrupted' ? MESSAGES.chat.turnStopped : status === 'failed' ? MESSAGES.chat.turnFailed : 'Turn outcome unconfirmed';
  const details: string[] = [];
  if (status !== 'running' && phaseLabel !== null) details.push(phaseLabel);
  if (backgroundTasks > 0) details.push(formatMessage(MESSAGES.chat.backgroundTasks, { count: backgroundTasks }));
  else if (status !== 'running' && hasIdleActivity) details.push(MESSAGES.chat.recentBackgroundActivity);
  const terminal = status === 'completed' || status === 'interrupted' || status === 'failed' ? status : undefined;
  return <ThreadStatusNotice messageActions={messageActions} detailsExpanded={detailsExpanded} onDetailsExpandedChange={onDetailsExpandedChange}
    heading={heading} details={details.length > 0 ? details : undefined}
    leading={status === 'running' || phaseLabel !== null ? leading : undefined}
    tone={status === 'failed' ? 'error' : status === 'unknown' || status === 'interrupted' ? 'warning' : 'neutral'}
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
