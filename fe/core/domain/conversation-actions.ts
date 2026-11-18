import { isUserConversationTurn, isOptimisticConversationTurn, isQueuedConversationTurn, type ConversationTurn, type TranscriptEntry } from './conversation.js';

/** A supplemental user entry can arrive after the stored outcome of its target turn. */
export function lastResponseEntryIndex(turns: readonly TranscriptEntry[]): number {
  return turns.findLastIndex(entry => !isUserConversationTurn(entry) || entry.supplemental !== true);
}

/** The last visible assistant message in this response; never an older response behind a new prompt. */
export function currentResponseMessage(turns: readonly TranscriptEntry[], terminal: boolean): ConversationTurn | null {
  const last = lastResponseEntryIndex(turns);
  for (let index = last; index >= 0; index -= 1) {
    const entry = turns[index];
    if (entry.author === 'turn' && index === last && terminal) continue;
    if (isQueuedConversationTurn(entry) || (isUserConversationTurn(entry) && entry.supplemental === true)) continue;
    if (entry.author === 'turn' || entry.author === 'system' || isUserConversationTurn(entry)) return null;
    if (entry.author === 'agent' && entry.text.trim() !== '') return entry;
  }
  return null;
}

/** The delivered prompt for this response; never an earlier automatic turn or queued message. */
export function latestUserMessage(turns: readonly TranscriptEntry[], terminal = true): ConversationTurn | null {
  const last = lastResponseEntryIndex(turns);
  for (let index = last; index >= 0; index -= 1) {
    const entry = turns[index];
    if (entry.author === 'turn' && index === last && terminal) continue;
    if (isQueuedConversationTurn(entry) || (isUserConversationTurn(entry) && entry.supplemental === true)) continue;
    if (entry.author === 'turn' || entry.author === 'system') return null;
    if (isUserConversationTurn(entry)) return isOptimisticConversationTurn(entry) ? null : entry;
  }
  return null;
}
