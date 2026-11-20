
import type { ConversationDisplayItem } from '../../../../core/domain/conversation-display.ts';
import type { TranscriptEntry } from '../../../../core/domain/conversation.ts';

/** Explicit imported presentation fixtures have no current input or execution authority. */
export function importedItems(): ConversationDisplayItem[] {
  const entries: TranscriptEntry[] = [
    { id: 'human:real', author: 'human', human: { userId: 'jian', displayName: 'Jian', messageId: 'real', revision: 1 }, text: 'Real input', atMs: 1 },
    { id: 'imported:80:0', author: 'unknown', imported: true, text: 'Imported saved words', atMs: 2 },
    { id: 'imported:81:0', author: 'agent', imported: true, text: 'Imported reply', atMs: 3 },
    { id: 'system', author: 'system', label: 'System update', text: 'System fact', atMs: 5 },
    { id: 'native', author: 'agent', text: 'Native reply', atMs: 6 },
    { id: 'imported:110:0', author: 'system', label: 'Imported history · System update', text: 'Imported system fact', atMs: 10 },
    { id: 'imported:111:0', author: 'activity', verb: 'Imported history · Ran', target: 'echo history', state: 'done', durationMs: null, detail: null, tool: null, atMs: 11 },
    ...(['failed', 'interrupted', 'completed'] as const).map((status, index): TranscriptEntry => ({ id: `imported:${100 + index}:0`, author: 'turn', imported: true,
      turnId: `old-${status}`, status, message: `Old ${status}`, text: `Old ${status}`, elapsedMs: null, atMs: 12 + index })),
  ];
  return entries.map(entry => ({ cursor: { contract: 'conversation_v1', source: 'presentation', card_id: 'daily-planner', position: entry.atMs }, source: { kind: 'entry', entry } }));
}
