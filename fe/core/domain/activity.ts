// Activity: the one vocabulary every indicator speaks. The kernel's `kernel/track/activity`
// overlay is the only source of "in motion / waiting on a person / broken" for a track; nothing else gets folded in.

import {
  parse, type NormalizedBlock, type NormalizedInline,
} from '../markdown/public.js';

/** What an indicator can show. `quiet` renders nothing. */
export type ActivityState = 'failed' | 'attention' | 'working' | 'unread' | 'quiet';

/** The kernel's verdict on whether a person has to act: nothing, give input, or repair. */
export type AttentionKind = 'none' | 'input' | 'failed';

/** The per-card verdict the overlay's `cards[]` carries; a card without one has no indicator. */
export type CardActivity = 'working' | 'input' | 'failed';

/** One question of an ask: the first option is the recommended one; none means a free answer. */
export type AskQuestion = Readonly<{ title: string; detail?: string; options: readonly string[] }>;

/**
 * Where an ask's answer goes: `wake` starts a new Planner turn; `hold` goes to a provider request
 * the running turn is paused on, and takes one of its options only.
 */
export type AskDelivery = 'wake' | 'hold';

type ActivityItemBase = Readonly<{
  /** The kernel's identity for it; the same source happening again is a new key. */
  key: string;
  /** The kernel's words: the Planner's questions, or the reason it stopped. */
  text: string;
  atMs: number;
}>;

export type AskId = string | number;

/**
 * One thing addressed to the user and not yet handled, as the kernel listed it (`items[]`). Only an
 * ask carries its id (the answer route's `ask_id`), its questions and its delivery.
 */
export type ActivityItem = ActivityItemBase & Readonly<{
  source: 'ask'; askId: AskId; questions: readonly AskQuestion[]; delivery: AskDelivery;
  action?: import('./presentation-types.js').AskAction;
}>;

function inlinePlainText(nodes: readonly NormalizedInline[]): string {
  return nodes.map((node): string => {
    switch (node.type) {
      case 'text': case 'inlineCode': case 'inlineMath': return node.value;
      case 'image': return node.alt;
      case 'break': return ' ';
      case 'html': return '';
      case 'link': case 'delete': case 'emphasis': case 'strong': return inlinePlainText(node.children);
    }
  }).join('');
}

function blockPlainText(block: NormalizedBlock): string {
  switch (block.type) {
    case 'heading': case 'paragraph': return inlinePlainText(block.children);
    case 'code': case 'math': return block.value;
    case 'blockquote': return block.children.map(blockPlainText).join(' ');
    case 'list': return block.children.map((item) => item.children.map(blockPlainText).join(' ')).join(' ');
    case 'table': return block.children
      .map((row) => row.children.map((cell) => inlinePlainText(cell.children)).join(' ')).join(' ');
    case 'html': case 'thematicBreak': return '';
  }
}

/**
 * An item's words as a reader hears them: the visible text of its markdown (no `**`, backticks or
 * link syntax), whitespace collapsed. Parsed by `core/markdown`; the raw text when that fails.
 */
export function notificationPlainText(markdown: string): string {
  const parsed = parse(markdown);
  if (parsed.status !== 'ready') return markdown;
  return parsed.value.children.map(blockPlainText).join(' ').replace(/\s+/g, ' ').trim();
}

/** The precedence, stated once: `failed > attention > working > unread > quiet`. */
export function activityStateOf(
  s: Readonly<{ working: boolean; attention: AttentionKind; unread: boolean }>,
): ActivityState {
  if (s.attention === 'failed') return 'failed';
  if (s.attention === 'input') return 'attention';
  if (s.working) return 'working';
  if (s.unread) return 'unread';
  return 'quiet';
}

/** The spoken counterpart of an indicator state — the ONE vocabulary every accessible label of an indicator draws from. `quiet` has nothing to say. */
export function activityLabelOf(state: ActivityState): string | null {
  switch (state) {
    case 'working': return 'Working';
    case 'attention': return 'Needs input';
    case 'failed': return 'Needs repair';
    case 'unread': return 'Unread updates';
    case 'quiet': return null;
  }
}

/** The activity bit a track row's accessible *name* carries; `unread` is never part of a name. Empty string, not `null`: the value is concatenated, never rendered alone. */
export function activityNameBit(state: ActivityState): string {
  switch (state) {
    case 'working': return 'working';
    case 'attention': return 'waiting on you';
    case 'failed': return 'needs attention';
    case 'unread':
    case 'quiet': return '';
  }
}

/** The one read of a track's per-card verdicts; `null` is "the kernel said nothing about this card". */
export function cardActivityOf(
  activity: Readonly<{ cards: Readonly<Record<string, CardActivity>> }>,
  cardId: string,
): CardActivity | null {
  return activity.cards[cardId] ?? null;
}

/** A card verdict as the attention axis reads it; `null` (no verdict) and `working` are `none`. */
export function attentionOfCard(card: CardActivity | null): AttentionKind {
  return card === 'input' ? 'input' : card === 'failed' ? 'failed' : 'none';
}

/** A card verdict as an indicator shows it. Cards have no read receipt, so `unread` is never part of a card-level state. */
export function cardActivityState(card: CardActivity): ActivityState {
  return activityStateOf({ working: card === 'working', attention: attentionOfCard(card), unread: false });
}

/** Stable list order breaks ties within each actionable priority. */
export function priorityConversation<T extends Readonly<{ id: string; isLead?: boolean; archivedAt?: number | null }>>(
  conversations: readonly T[], cards: Readonly<Record<string, CardActivity>>, unread: (conversation: T) => boolean,
): T | undefined {
  const active = conversations.filter(c => c.archivedAt == null);
  return active.find(c => cards[c.id] === 'failed') ?? active.find(c => cards[c.id] === 'input')
    ?? active.find(unread) ?? active.find(c => c.isLead);
}
