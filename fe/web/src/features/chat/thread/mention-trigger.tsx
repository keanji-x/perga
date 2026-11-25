import { withEmptyTrigger } from '../../../ui/empty-state/astryx-trigger.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
// Astryx's `@` menu over plugin guides and available Area tags, reports and blocks.
// The caller owns the search scope; this feature owns rows, chips and answer freshness.

import { useMemo, useRef } from 'react';
import type { ChatComposerToken, ChatComposerTrigger } from '@astryxdesign/core/Chat';
import type { SearchableItem, SearchSource } from '@astryxdesign/core/Typeahead';
import { Badge } from '@astryxdesign/core/Badge';

import type { MentionKind, MentionSearch, MentionSuggestion } from '../../../../../core/domain/mentions.ts';
import styles from './mention-trigger.module.css';

/** The headings Astryx's `groupItems` draws, in the order the suggestions arrive. */
const GROUP_HEADING: Readonly<Record<MentionKind, string>> = Object.freeze({
  template: 'Templates',
  tag: 'Tags',
  track: 'Tracks',
  block: 'Blocks',
  plugin: 'Plugins',
  memory: 'Memory',
});

const ITEM_MARKER: Readonly<Record<MentionKind, string>> = Object.freeze({
  template: ':', tag: '#', track: '/', block: '>', plugin: '+', memory: '@',
});

/**
 * How long a keystroke waits before its request goes out. The wait is this source's, not
 * Astryx's: `useTriggerMenu` calls `search('')` on every keystroke only to learn whether the
 * source is async, discards the answer, and calls `cancel()` right before the real search, so
 * a source that fetched at once would send a second, empty-query request per keystroke. A
 * composer carrying this trigger therefore runs `ChatComposerInput` with `debounceMs={0}`:
 * Astryx's own delay would let that probe's timer fire before the `cancel()` arrives.
 */
export const MENTION_SEARCH_DELAY_MS = 120;

type MentionCategory = Readonly<{
  kind: MentionKind; label: string; detail: string; prefix: string;
}>;

type MentionItem = SearchableItem<
  | Readonly<{ group?: string; suggestion: MentionSuggestion; preview: boolean }>
  | Readonly<{ category: MentionCategory; more: boolean }>
>;

function categoryItems(suggestions: readonly MentionSuggestion[], memoryEnabled: boolean): MentionItem[] {
  return ([
    { kind: 'template', label: 'Templates', detail: 'Track starting point', prefix: '@:' },
    { kind: 'plugin', label: 'Plugins', detail: 'Plugin guides', prefix: '@+' },
    { kind: 'tag', label: 'Tags', detail: 'Group of tracks', prefix: '@#' },
    { kind: 'track', label: 'Tracks', detail: 'Full report', prefix: '@/' },
    { kind: 'block', label: 'Blocks', detail: 'Report section', prefix: '@>' },
    { kind: 'memory', label: 'Memory', detail: 'Area and global notes', prefix: '@memory:' },
  ] satisfies MentionCategory[]).filter(category => category.kind !== 'memory' || memoryEnabled).flatMap((category): MentionItem[] => {
    const examples = suggestions.filter((suggestion) => suggestion.kind === category.kind).slice(0, 2);
    return [
      { id: `category:${category.kind}`, label: category.label, auxiliaryData: { category, more: false } },
      ...examples.map((suggestion) => itemOf(suggestion, true)),
      ...(examples.length === 0 ? [] : [{
        id: `more:${category.kind}`, label: `More ${category.label}`, auxiliaryData: { category, more: true },
      }]),
    ];
  });
}

function itemOf(suggestion: MentionSuggestion, preview = false): MentionItem {
  return {
    id: suggestion.id,
    label: suggestion.label,
    auxiliaryData: { ...(preview ? {} : { group: GROUP_HEADING[suggestion.kind] }), suggestion, preview },
  };
}

/**
 * The chip a pick becomes: its `value` is what the composer serializes, so the sent text holds
 * the source's `insert` exactly; the reader sees the short `chip` form instead.
 */
export function mentionToken(suggestion: MentionSuggestion): ChatComposerToken {
  // Prefer Astryx's structured Badge token. Only fall back to custom render when a
  // serialized reference exceeds Astryx's 200-char pasted-text heuristic (e.g. plugin docs).
  if (suggestion.insert.length <= 200) {
    return { value: suggestion.insert, label: suggestion.chip, variant: 'neutral' };
  }
  return { value: suggestion.insert, render: () => <Badge label={suggestion.chip} variant="neutral" /> };
}

/**
 * An async `SearchSource` whose answers are only ever the latest search's.
 *
 * Astryx sets whatever a search resolves to, with no check of its own that the search is still
 * the current one, so the guard lives here: a search superseded by a newer one or ended by
 * `cancel()` aborts its request and its promise never settles. A failed name search
 * answers `[]`; bare `@` keeps its category entrances with no examples.
 * `bootstrap` is not used by the trigger menu.
 */
export function createMentionSource(search: MentionSearch, delayMs: number, memoryEnabled = true): SearchSource<MentionItem> {
  type Attempt = { timer: ReturnType<typeof setTimeout> | null; controller: AbortController };
  let current: Attempt | null = null;
  const cancel = () => {
    if (current === null) return;
    if (current.timer !== null) clearTimeout(current.timer);
    current.controller.abort();
    current = null;
  };
  return {
    bootstrap: () => [],
    cancel,
    search(query) {
      cancel();
      const attempt: Attempt = { timer: null, controller: new AbortController() };
      current = attempt;
      return new Promise<MentionItem[]>((resolve) => {
        const answer = (items: MentionItem[]) => {
          if (current === attempt) resolve(items);
        };
        attempt.timer = setTimeout(() => {
          attempt.timer = null;
          search(query, attempt.controller.signal).then(
            (suggestions) => { answer(query === '' ? categoryItems(suggestions, memoryEnabled) : suggestions.map((suggestion) => itemOf(suggestion))); },
            () => { answer(query === '' ? categoryItems([], memoryEnabled) : []); },
          );
        }, delayMs);
      });
    },
  };
}

function MentionRow({ suggestion, preview }: { suggestion: MentionSuggestion; preview: boolean }) {
  return (
    <span className={styles.item} data-nc-mention={suggestion.kind}>
      {preview && <span className={styles.itemMarker} aria-hidden="true">
        {ITEM_MARKER[suggestion.kind]}
      </span>}
      <span className={styles.label}>{preview && suggestion.kind === 'tag' ? suggestion.label.slice(1) : suggestion.label}</span>
      {suggestion.detail !== null && <span className={styles.detail}>{suggestion.detail}</span>}
    </span>
  );
}

/** Astryx closes after inserting text. Re-open the typed prefix in the same field on
 * the next frame, once insertion and reset have finished; never move focus back. */
function enterCategory(category: MentionCategory): string {
  const node = window.getSelection()?.anchorNode;
  const element = node instanceof Element ? node : node?.parentElement;
  const editable = element?.closest<HTMLElement>('[contenteditable="true"]');
  if (editable !== undefined && editable !== null) {
    requestAnimationFrame(() => {
      if (editable.isConnected && document.activeElement === editable) {
        const selection = window.getSelection();
        if (selection === null || !selection.isCollapsed || selection.rangeCount === 0) return;
        const range = selection.getRangeAt(0);
        // Astryx 0.7.12+ places the caret inside the inserted text node; earlier
        // versions left it after the node on the parent element. Accept both.
        if (range.startContainer.nodeType === Node.TEXT_NODE) {
          const text = range.startContainer.textContent ?? '';
          if (!text.slice(0, range.startOffset).endsWith(category.prefix)) return;
        } else {
          const inserted = range.startContainer.childNodes[range.startOffset - 1];
          if (inserted?.nodeType !== Node.TEXT_NODE || inserted.textContent !== category.prefix) return;
          range.setStart(inserted, category.prefix.length);
          range.collapse(true);
          selection.removeAllRanges();
          selection.addRange(range);
        }
        editable.dispatchEvent(new Event('input', { bubbles: true }));
      }
    });
  }
  return category.prefix;
}

function CategoryRow({ category }: { category: MentionCategory }) {
  return (
    <span className={`${styles.item} ${styles.categoryHead}`} data-nc-mention-category={category.kind}>
      <span className={styles.categoryTitle}>{category.label}</span>
      <span className={styles.description}>{category.detail}</span>
    </span>
  );
}

function MoreRow({ category }: { category: MentionCategory }) {
  return <span className={`${styles.item} ${styles.more}`}>
    <span aria-hidden="true">…</span>
    <span className={styles.visuallyHidden}>More {category.label}</span>
  </span>;
}

export function mentionTrigger(source: SearchSource<MentionItem>): ChatComposerTrigger {
  return withEmptyTrigger({
    character: '@',
    searchSource: source,
    menuLabel: 'Mention',
    renderItem: (item) => {
      const data = (item as MentionItem).auxiliaryData!;
      if ('category' in data) return data.more ? <MoreRow category={data.category} /> : <CategoryRow category={data.category} />;
      return <MentionRow suggestion={data.suggestion} preview={data.preview} />;
    },
    onSelect: (item) => {
      const data = (item as MentionItem).auxiliaryData!;
      return 'category' in data ? enterCategory(data.category) : mentionToken(data.suggestion);
    },
  }, { icon: <Icon name="search" />, title: 'No matches', description: 'Try another keyword or reference type.' });
}

/**
 * The `@` trigger, or `undefined` when the host disables references with `search: null`.
 *
 * Stable for as long as `search` stays non-null, whatever its identity: `useTriggerMenu`
 * compares the active trigger by identity on every input event, so a new object per render
 * would close the menu mid-query. Each search reads the latest `search`, so a change of
 * area or track applies from the next keystroke.
 */
export function useMentionTrigger(search: MentionSearch | null, memoryEnabled = true): ChatComposerTrigger | undefined {
  const searchRef = useRef(search);
  searchRef.current = search;
  const enabled = search !== null;
  return useMemo(() => {
    if (!enabled) return undefined;
    const latest: MentionSearch = (query, signal) => searchRef.current?.(query, signal) ?? Promise.resolve([]);
    return mentionTrigger(createMentionSource(latest, MENTION_SEARCH_DELAY_MS, memoryEnabled));
  }, [enabled, memoryEnabled]);
}
