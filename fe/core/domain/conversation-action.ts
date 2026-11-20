import type { SessionItemsPageDto } from '../api/perga-v1.js';

export type ConversationAction = Readonly<
  | { kind: 'readError'; message: string }
  | { kind: 'readonly'; reason: NonNullable<SessionItemsPageDto['readonlyReason']>; canReopen: boolean }
  | { kind: 'unknown'; detail: SessionItemsPageDto['unknownDetail'] }
  | { kind: 'ask' }
  | { kind: 'sendFailed'; unconfirmed: boolean }
  | { kind: 'actionError'; message: string }
  | { kind: 'workerPaused' }
>;

/** One action slot. Persisted state outranks asks and local transport feedback. */
export function deriveConversationAction(input: Readonly<{
  readError: string | null;
  readonlyReason: SessionItemsPageDto['readonlyReason'];
  canReopen: boolean;
  state: SessionItemsPageDto['state'] | null;
  unknownDetail: SessionItemsPageDto['unknownDetail'];
  hasAsks: boolean;
  localSend: 'idle' | 'sending' | 'failed';
  sendUnconfirmed: boolean;
  actionError: string | null;
  workerPaused: boolean;
}>): ConversationAction | null {
  if (input.readError !== null) return { kind: 'readError', message: input.readError };
  if (input.readonlyReason !== null) return { kind: 'readonly', reason: input.readonlyReason, canReopen: input.canReopen };
  if (input.state === 'unknown') return { kind: 'unknown', detail: input.unknownDetail };
  if (input.hasAsks) return { kind: 'ask' };
  if (input.localSend === 'failed') return { kind: 'sendFailed', unconfirmed: input.sendUnconfirmed };
  if (input.actionError !== null) return { kind: 'actionError', message: input.actionError };
  if (input.workerPaused) return { kind: 'workerPaused' };
  return null;
}

/** Shared execution policy for overflow actions and slash commands. */
export function conversationCommandEnabled(command: 'stop' | 'compact' | 'reconnect' | 'reset' | 'new',
  state: SessionItemsPageDto['state'] | null, readonlyReason: SessionItemsPageDto['readonlyReason'],
  detail: SessionItemsPageDto['unknownDetail'], recovering: boolean,
  capabilities: Readonly<{ canStop: boolean; manualCompactSupported: boolean; canResetContext: boolean }>): boolean {
  if (command === 'new') return true;
  if (readonlyReason !== null || recovering) return false;
  if (command === 'stop') return capabilities.canStop;
  if (command === 'compact') return state === 'idle' && capabilities.manualCompactSupported;
  if (command === 'reconnect') return state === 'unknown' && detail?.canReconnect === true;
  return capabilities.canResetContext && (state === 'idle' || (state === 'unknown' && detail?.canResetContext === true));
}

/** Startup identity exists before the provider becomes an active turn. */
export function conversationStopTurn(run: Pick<SessionItemsPageDto, 'activeTurn' | 'lastTurn'> | undefined,
  acceptedTurnId: string | null): string | null {
  if (run?.activeTurn?.status === 'running') return run.activeTurn.id;
  const last = run?.lastTurn;
  if (last?.status === 'queued' || last?.status === 'starting' || last?.status === 'running') return last.id;
  return last?.id === acceptedTurnId ? null : acceptedTurnId;
}
