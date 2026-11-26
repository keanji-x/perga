import { Fzf, byLengthAsc, extendedMatch, type FzfOptions, type FzfResultItem } from 'fzf';

import { fileParentDirectory, isAbsoluteFilePath, isFileOpenSpelling, joinFilePath, splitFileOpenInput, type ReportFileLinkTarget } from './report-file.js';
import type { DirectoryListingWire } from './fs.js';

export type QuickOpenQuery =
  | Readonly<{ kind: 'search'; query: string; target: ReportFileLinkTarget }>
  | Readonly<{ kind: 'directory'; directory: string; prefix: string; target: ReportFileLinkTarget }>;

export function quickOpenQuery(input: string): QuickOpenQuery {
  const target = splitFileOpenInput(input.trimStart());
  if ((isAbsoluteFilePath(target.path) || /^(?:~|\.\.?)\/|\/\.\.\//.test(target.path)) && isFileOpenSpelling(target.path)) {
    const separator = target.path.lastIndexOf('/');
    const directory = fileParentDirectory(target.path) ?? target.path;
    return { kind: 'directory', directory, prefix: target.path.slice(separator + 1), target };
  }
  return { kind: 'search', query: target.path.trim(), target };
}

export type QuickOpenEntry = Readonly<{
  path: string;
  name: string;
  directory: string;
  /** UTF-16 positions in the original path, including its directory prefix. */
  positions: ReadonlySet<number>;
  group: 'Recent' | 'Files' | null;
  isDirectory: boolean;
}>;

const resultLimit = 50;
const shortlistLimit = 500;
const basenameBucketLimit = 250;

function entryFor(path: string, positions: ReadonlySet<number>, group: QuickOpenEntry['group'], isDirectory = false): QuickOpenEntry {
  const offset = path.lastIndexOf('/') + 1;
  return { path, name: path.slice(offset), directory: fileParentDirectory(path) ?? '', positions, group, isDirectory };
}

/** fzf indexes NFC-normalized text. Preserve offsets in the actual filesystem spelling. */
function originalPositions(text: string, positions: ReadonlySet<number>): ReadonlySet<number> {
  if (positions.size === 0 || text.normalize() === text) return positions;
  const mapped = new Set<number>();
  let normalizedOffset = 0;
  for (const { segment, index } of new Intl.Segmenter().segment(text)) {
    const end = normalizedOffset + segment.normalize().length;
    for (; normalizedOffset < end; normalizedOffset += 1) {
      if (!positions.has(normalizedOffset)) continue;
      for (let i = 0; i < segment.length; i += 1) mapped.add(index + i);
    }
  }
  return mapped;
}

function allPositions(text: string, offset = 0): Set<number> {
  return new Set(Array.from({ length: text.length }, (_, index) => offset + index));
}

function matchesBasename(match: FzfResultItem<string>): boolean {
  return match.end > match.item.normalize().lastIndexOf('/') + 1;
}

function compareBasename(a: FzfResultItem<string>, b: FzfResultItem<string>): number {
  return Number(matchesBasename(b)) - Number(matchesBasename(a));
}

type FileIndex = Readonly<{
  paths: readonly string[];
  find: (query: string) => FzfResultItem<string>[];
}>;

type AsciiPath = Readonly<{ path: string; lower: string; basenameStart: number }>;
type AsciiTerm = Readonly<{ text: string; caseSensitive: boolean }>;

function asciiTermScore(candidate: AsciiPath, term: AsciiTerm): number | undefined {
  const text = term.caseSensitive ? candidate.path : candidate.lower;
  let start = text.lastIndexOf(term.text);
  let end = start + term.text.length - 1;
  if (start < 0) {
    // A tight backwards subsequence is cheap to find with native string scans.
    end = text.lastIndexOf(term.text[term.text.length - 1]);
    if (end < 0) return undefined;
    start = end;
    for (let i = term.text.length - 2; i >= 0; i -= 1) {
      if (start === 0) return undefined;
      start = text.lastIndexOf(term.text[i], start - 1);
      if (start < 0) return undefined;
    }
  }

  const gaps = end - start + 1 - term.text.length;
  const previous = candidate.lower.charCodeAt(start - 1);
  const boundary = !((previous >= 97 && previous <= 122) || (previous >= 48 && previous <= 57));
  const camelCase = !boundary && candidate.path[start - 1] === candidate.lower[start - 1]
    && candidate.path[start] !== candidate.lower[start];
  return (boundary ? 16 : camelCase ? 14 : 0) + (end >= candidate.basenameStart ? 12 : 0)
    + (gaps === 0 ? 4 * (term.text.length - 1) : -gaps);
}

function shortlistAscii(paths: readonly AsciiPath[], query: string, limit: number): string[] {
  const terms = query.split(/ +/).map(text => ({ text, caseSensitive: text !== text.toLowerCase() }));
  const candidates: { path: string; score: number }[] = [];
  for (const path of paths) {
    let score = 0;
    let matched = true;
    for (const term of terms) {
      const termScore = asciiTermScore(path, term);
      if (termScore === undefined) {
        matched = false;
        break;
      }
      score += termScore;
    }
    if (matched) candidates.push({ path: path.path, score });
  }
  if (candidates.length > limit) {
    candidates.sort((a, b) => b.score - a.score || a.path.length - b.path.length);
    candidates.length = limit;
  }
  return candidates.map(candidate => candidate.path);
}

/** Prepare basename buckets and ASCII paths once per index load. */
export function createQuickOpenFileIndex(paths: readonly string[]): FileIndex {
  const all = new Set(paths);
  const unique = [...all];
  const large = unique.length > shortlistLimit * 4;
  const basenames = new Map<string, string[]>();
  const ascii: AsciiPath[] = [];
  const unicode: string[] = [];
  for (const path of unique) {
    const basenameStart = path.lastIndexOf('/') + 1;
    const name = path.slice(basenameStart).normalize().toLowerCase();
    const bucket = basenames.get(name);
    if (bucket === undefined) basenames.set(name, [path]);
    else bucket.push(path);
    if (!large) continue;
    if (/[\u0080-\uffff]/.test(path)) unicode.push(path);
    else ascii.push({ path, lower: path.toLowerCase(), basenameStart });
  }
  for (const bucket of basenames.values()) {
    if (bucket.length <= basenameBucketLimit) continue;
    bucket.sort((a, b) => a.length - b.length);
    bucket.length = basenameBucketLimit;
  }
  const v2Options: FzfOptions<string> = { match: extendedMatch, forward: false, sort: false };
  const v1Options: FzfOptions<string> = { match: extendedMatch, forward: false, fuzzy: 'v1',
    limit: shortlistLimit, tiebreakers: [compareBasename, byLengthAsc] };
  const smallFzf = large ? undefined : new Fzf(unique, v2Options);
  let fzf: Fzf<string[]> | undefined;
  let unicodeFzf: Fzf<string[]> | undefined;

  return { paths: unique, find: query => {
    const key = query.normalize().toLowerCase();
    const exactNames = basenames.get(key) ?? [];
    const exactPath = all.has(query);
    let matches: FzfResultItem<string>[];
    if (smallFzf === undefined) {
      // Native string scans avoid fzf's per-match allocations for common ASCII
      // terms. Let fzf handle extended syntax and Unicode before the V2 pass.
      const plainAscii = /^[a-z0-9._/-]+(?: +[a-z0-9._/-]+)*$/i.test(query);
      const candidates = new Set<string>();
      if (exactPath) candidates.add(query);
      for (const name of new Set([key, ...key.split(/ +/)])) {
        if (candidates.size === shortlistLimit) break;
        for (const path of basenames.get(name) ?? []) {
          candidates.add(path);
          if (candidates.size === shortlistLimit) break;
        }
      }
      // A popular exact basename already supplies many strong candidates.
      const fuzzyLimit = exactNames.length === basenameBucketLimit ? basenameBucketLimit : shortlistLimit;
      const fuzzy = plainAscii ? shortlistAscii(ascii, query, fuzzyLimit)
        : (fzf ??= new Fzf(unique, v1Options)).find(query).map(match => match.item);
      for (const path of fuzzy) {
        candidates.add(path);
      }
      if (plainAscii && unicode.length > 0) {
        for (const match of (unicodeFzf ??= new Fzf(unicode, v1Options)).find(query)) {
          candidates.add(match.item);
        }
      }
      matches = new Fzf([...candidates], v2Options).find(query);
    } else {
      matches = smallFzf.find(query);
    }

    // fzf syntax can also be an exact path or basename on disk.
    if (exactNames.length > 0 || exactPath) {
      const literals = exactPath ? new Set([query, ...exactNames]) : exactNames;
      const matched = new Set(matches.map(match => match.item));
      for (const path of literals) {
        if (matched.has(path) || (path !== query && path.slice(path.lastIndexOf('/') + 1) !== query)) continue;
        const normalized = path.normalize();
        const start = path === query ? 0 : normalized.lastIndexOf('/') + 1;
        matches.push({ item: path, start, end: normalized.length, score: 0,
          positions: allPositions(normalized.slice(start), start) });
      }
    }

    return matches;
  } };
}

function identity(path: string): string { return path; }

export function quickOpenFileEntries(
  query: string, recent: FileIndex, workspace: FileIndex, supplemental?: FileIndex,
): QuickOpenEntry[] {
  const recentOrder = new Map(recent.paths.map((path, index) => [path, index]));
  if (query === '') {
    const files: QuickOpenEntry[] = [];
    for (const path of workspace.paths) {
      if (recentOrder.has(path)) continue;
      files.push(entryFor(path, new Set(), 'Files'));
      if (files.length === resultLimit) break;
    }
    return [...recent.paths.map(path => entryFor(path, new Set(), 'Recent')), ...files];
  }

  const needle = query.toLowerCase();
  const matches = new Map<string, FzfResultItem<string>>();
  for (const index of [workspace, recent, supplemental]) {
    for (const match of index?.find(query) ?? []) matches.set(match.item, match);
  }
  return Array.from(matches.values(), match => {
    const path = match.item;
    const name = path.slice(path.lastIndexOf('/') + 1);
    const mru = recentOrder.get(path);
    const score = match.score + (name.toLowerCase() === needle ? 24 : matchesBasename(match) ? 12 : 0)
      + (mru === undefined ? 0 : Math.max(2, 10 - mru));
    return { match, score, exact: path === query, mru: mru ?? recent.paths.length };
  }).sort((a, b) => Number(b.exact) - Number(a.exact) || b.score - a.score || a.mru - b.mru
    || byLengthAsc(a.match, b.match, identity))
    .slice(0, resultLimit)
    .map(({ match }) => entryFor(match.item, originalPositions(match.item, match.positions), null));
}

export function quickOpenDirectoryEntries(directory: string, prefix: string, entries: DirectoryListingWire['entries']): QuickOpenEntry[] {
  const lower = prefix.toLowerCase();
  const fzf = new Fzf(entries, { selector: entry => entry.name, match: extendedMatch, tiebreakers: [byLengthAsc] });
  const matches = fzf.find(prefix);
  // Prefer literal exact names, then smart-case prefixes, then fuzzy matches.
  const rank = (name: string) => {
    if (name === prefix) return 0;
    const candidate = lower === prefix ? name.toLowerCase() : name;
    if (candidate === prefix) return 1;
    return candidate.startsWith(prefix) ? 2 : 3;
  };
  if (prefix !== '' && !matches.some(match => match.item.name === prefix)) {
    const exact = entries.find(entry => entry.name === prefix);
    if (exact !== undefined)
      matches.push({ item: exact, start: 0, end: prefix.length, score: 0, positions: allPositions(prefix.normalize()) });
  }
  return matches.sort((a, b) => rank(a.item.name) - rank(b.item.name) || (prefix === ''
    ? Number(b.item.is_dir) - Number(a.item.is_dir) || a.item.name.localeCompare(b.item.name) : 0))
    .slice(0, resultLimit)
    .map(({ item, positions }) => {
      const path = joinFilePath(directory, item.name);
      const offset = path.length - item.name.length;
      const pathPositions = new Set(Array.from(originalPositions(item.name, positions), position => offset + position));
      return entryFor(path, pathPositions, null, item.is_dir);
    });
}
