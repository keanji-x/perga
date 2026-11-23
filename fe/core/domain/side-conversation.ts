import { isOptimisticConversationTurn, type TranscriptEntry, type SideConversation } from './conversation.js';

/** A discussion source is a frozen excerpt, never a provider session or live subscription. */
const CONTEXT_LIMIT = 12_000;
const OMITTED = '[Earlier text omitted]\n\n';

/** Loaded, delivered speech only. No tools, system messages or attachment paths travel. */
export function sideConversationSnapshot(sourceCardId: string, entries: readonly TranscriptEntry[]): SideConversation {
  const text = entries.flatMap((entry) => {
    if (entry.author !== 'you' && entry.author !== 'human' && entry.author !== 'agent') return [];
    if (isOptimisticConversationTurn(entry) || entry.deliveryError !== undefined
      || (entry.messageStatus !== undefined && entry.messageStatus !== 'completed' && entry.messageStatus !== 'processing')) return [];
    return [`${entry.author === 'agent' ? 'Assistant' : 'User'}: ${entry.text}`];
  }).join('\n\n');
  const chars = Array.from(text);
  return { source_card_id: sourceCardId,
    context: chars.length <= CONTEXT_LIMIT ? text
      : OMITTED + chars.slice(-(CONTEXT_LIMIT - OMITTED.length)).join('') };
}
