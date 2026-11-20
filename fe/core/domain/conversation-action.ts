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
  detail: SessionItemsPageDto['unknownDetail'], recovering: boolean): boolean {
  if (command === 'new') return true;
  if (readonlyReason !== null || recovering) return false;
  if (command === 'stop') return state === 'running';
  if (command === 'compact') return state === 'idle';
  if (command === 'reconnect') return state === 'unknown' && detail?.canReconnect === true;
  return state === 'idle' || (state === 'unknown' && detail?.canResetContext === true);
}
