// Mention candidates: ranked Area report references and plugin catalog documentation.
// Report addresses come from the server; plugin reference text is built from catalog metadata.

import type { TrackTemplate } from './track.js';
import { parseMemoryMentionAddress, type MemoryMention } from './memory.js';

import type { ApiAbortSignal } from '../api/types.js';

export type MentionKind = 'tag' | 'track' | 'block' | 'plugin' | 'template' | 'memory';

/** One row of the `@` menu and the chip it becomes. */
export type MentionSuggestion = Readonly<{
  /** Unique across the whole list, so all groups can share one keyed list. */
  id: string;
  kind: MentionKind;
  /** The row's name. */
  label: string;
  /** The row's secondary text, or `null` when the name says everything. */
  detail: string | null;
  /** What the chip shows in the field. */
  chip: string;
  /** The exact text the message carries for this pick, from its candidate producer. */
  insert: string;
}>;

/** The `@` source's port: the ranked suggestions for the text typed after `@`, type prefix included, abandoned when `signal` aborts. */
export type MentionSearch = (query: string, signal: ApiAbortSignal) => Promise<readonly MentionSuggestion[]>;

/**
 * What the text typed after `@` asks for. A leading `:`, `+`, `#`, `/` or `>` narrows the menu to templates, plugins,
 * tags, tracks or blocks (`kind`) and is not part of the search: `text` is the rest, the server's `q`.
 * Without one of them `kind` is `null` and `text` is everything typed, so a name that starts
 * with one of these characters is still found by typing it without that character.
 */
export type MentionQuery = Readonly<{ kind: MentionKind | null; text: string }>;

/** Also accepts a Chinese IME's punctuation: `：`, `＋`, `＃`, `、` and `》`. */
function prefixKind(char: string): MentionKind | null {
  switch (char) {
    case ':': case '：': return 'template';
    case '+': case '＋': return 'plugin';
    case '#': case '＃': return 'tag';
    case '/': case '、': return 'track';
    case '>': case '》': return 'block';
    default: return null;
  }
}

export function mentionQueryOf(typed: string): MentionQuery {
  const memory = /^(?:memory|mem)(?:[:：](.*)|$)/i.exec(typed);
  if (memory !== null) return { kind: 'memory', text: memory[1] ?? '' };
  const kind = prefixKind(typed.charAt(0));
  return kind === null ? { kind, text: typed } : { kind, text: typed.slice(1) };
}

/** A persisted message keeps the server address, not the composer's transient chip metadata. */
export type SentMentionPart = Readonly<{ text: string; label: string | null; memoryScope?: MemoryMention['scope'] }>;

function memoryLabel({ scope, key }: Pick<MemoryMention, 'scope' | 'key'>): string {
  const title = scope === 'area' ? 'Area' : 'Global';
  return key === null ? `${title} Memory` : `${title} · ${key}`;
}

export function memoryMentionSuggestion(mention: MemoryMention): MentionSuggestion {
  return {
    id: `memory:${mention.ref}`, kind: 'memory', label: mention.title,
    detail: mention.key === null ? null : [memoryLabel(mention), mention.summary].filter(Boolean).join(' · '),
    chip: memoryLabel(mention), insert: mention.insert,
  };
}

function sentMentionLabel(address: string): string | null {
  if (/^template:[^\s`\p{Cc}]+$/u.test(address)) return address.slice(9);
  if (address.startsWith('tag:')) {
    const tag = address.slice(4);
    // Stored tags obey report_tags::normalize_tag; malformed prefixes must not consume later picks.
    if (tag === '' || /[\s,\p{Cc}]/u.test(tag) || Array.from(tag).length > 64) return null;
    return `#${tag}`;
  }
  // Display the server's path shape only: no identity lookup, canonicality or authorization.
  const canonical = /^\/[^/]+\/([^/]+)\/report\.md(?:#([^\s]+))?$/.exec(address);
  const report = canonical ?? /^area\/reports\/(.+)\.md(?:#([^\s]+))?$/.exec(address);
  if (report === null) return null;
  let name: string;
  try { name = decodeURIComponent(report[1]); } catch { return null; }
  // Block headings are not persisted in the message. Keep its stable ID visible instead.
  return report[2] === undefined ? name : `${name} › ${report[2]}`;
}

/**
 * Recover display pills without fetching today's candidates or changing the sent text.
 * Match complete backtick runs, including the longer fences and edge padding emitted by
 * the server. Unknown addresses and unfinished spans remain literal user text.
 */
function parseMentionParts(text: string, onMention?: (address: string) => void): readonly SentMentionPart[] {
  const parts: SentMentionPart[] = [];
  const openings = /@(`+)/g;
  let end = 0;
  for (let opening = openings.exec(text); opening !== null; opening = openings.exec(text)) {
    const start = openings.lastIndex;
    const fences = /`+/g;
    fences.lastIndex = start;
    let closing = fences.exec(text);
    while (closing !== null && closing[0].length !== opening[1].length) closing = fences.exec(text);
    if (closing === null) continue;
    let address = text.slice(start, closing.index);
    if (address.startsWith(' ') && address.endsWith(' ')) address = address.slice(1, -1);
    const memory = parseMemoryMentionAddress(address);
    const label = memory === null ? sentMentionLabel(address) : memoryLabel(memory);
    if (label === null) continue;
    onMention?.(address);
    openings.lastIndex = fences.lastIndex;
    if (opening.index > end) parts.push({ text: text.slice(end, opening.index), label: null });
    end = fences.lastIndex;
    parts.push({ text: text.slice(opening.index, end), label, ...(memory === null ? {} : { memoryScope: memory.scope }) });
  }
  if (end < text.length) parts.push({ text: text.slice(end), label: null });
  return parts;
}

/** Recover display chips using the same parser as template extraction. */
export function sentMentionParts(text: string): readonly SentMentionPart[] {
  return parseMentionParts(text);
}

/** Template IDs in message order, preserving repeated mentions. */
export function extractTemplateMentions(text: string): readonly string[] {
  const ids: string[] = [];
  parseMentionParts(text, address => {
    if (address.startsWith('template:')) ids.push(address.slice(9));
  });
  return ids;
}

function hasEmptyInputSchema(schema: unknown): boolean {
  if (schema === undefined) return true;
  if (schema === null || typeof schema !== 'object' || Array.isArray(schema)) return false;
  return Object.keys(schema).length === 0;
}

/** Return a template ID for `POST /api/tracks` only when the message names one known ready template without inputs. */
export function selectedReadyTemplateId(
  text: string,
  templates: readonly TrackTemplate[],
): string | undefined {
  const templateIds = extractTemplateMentions(text);
  if (templateIds.length !== 1) return undefined;
  const template = templates.find(candidate => candidate.id === templateIds[0]);
  return template?.availability.status === 'ready' && hasEmptyInputSchema(template.input_schema)
    ? template.id
    : undefined;
}

/** Find templates by title, ID or description without requiring a type prefix. */
export function templateMentionSuggestions(templates: readonly TrackTemplate[], typed: string): readonly MentionSuggestion[] {
  const query = mentionQueryOf(typed);
  if (query.kind !== null && query.kind !== 'template') return [];
  const needle = query.text.trim().toLocaleLowerCase();
  return templates.filter(template => `${template.title} ${template.id} ${template.description ?? ''}`.toLocaleLowerCase().includes(needle))
    .sort((a, b) => a.title.localeCompare(b.title) || a.id.localeCompare(b.id))
    .slice(0, 8).map(template => ({
      id: `template:${template.id}`, kind: 'template', label: template.title,
      chip: template.title, detail: template.description, insert: `@\`template:${template.id}\``,
    }));
}
