import { isUserConversationTurn, type PlannerQueueWriteOutcome, type TranscriptEntry } from './conversation.js';

/** Count each canonical pending identity once, whether listed or reference-only. */
export function unlistedPendingInputCount(inputs: readonly import('../api/generated/wire.js').ConversationPendingInput[], entries: readonly import('./conversation.js').PendingQueueEntry[]): number {
  return inputs.filter(input => input.kind !== 'human' || !entries.some(entry => entry.entry_id === input.message_id && entry.rev >= input.revision)).length;
}

/** A newer immutable Human version updates an existing listed row, never invents pending work. */
export function pendingQueueWithCurrentHumanVersions(entries: readonly import('./conversation.js').PendingQueueEntry[], turns: readonly import('./conversation.js').ConversationMessage[]): readonly import('./conversation.js').PendingQueueEntry[] {
  let changed=false;
  const result=entries.map(entry=>{
    const source=turns.find(turn=>turn.author === 'you' && turn.human?.messageId === entry.entry_id
      && turn.human.revision > entry.rev);
    if(source === undefined || source.author === 'system' || source.human === undefined)return entry;
    changed=true;
    return {...entry,rev:source.human.revision,text:source.text};
  });
  return changed ? result : entries;
}

/** A listed queue version owns older-or-equal transcript carriers of that same immutable message. */
export function pendingQueueOwnsHumanEntry(entry: TranscriptEntry, versions: ReadonlyMap<string,number>): boolean {
  if(!isUserConversationTurn(entry) || entry.human === undefined)return false;
  const queued=versions.get(entry.human.messageId);
  return queued !== undefined && queued >= entry.human.revision;
}

/** Unified steering receipts use `unknown` when the provider did not confirm delivery. */
export function sessionSteeringOutcome(delivery: string): PlannerQueueWriteOutcome {
  if (delivery === 'queued' || delivery === 'rejected') return { kind: 'not_running' };
  if (delivery === 'unknown') return { kind: 'unanswered' };
  return { kind: 'done' };
}
