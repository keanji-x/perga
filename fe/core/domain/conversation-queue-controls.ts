import { isUserConversationTurn, type TranscriptEntry } from './conversation.js';
import { type PlannerQueueWriteOutcome } from './conversation-commands.js';

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
