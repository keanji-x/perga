import { extendedMatch, Fzf } from 'fzf';
import { expect, it } from 'vitest';
import { createQuickOpenFileIndex, quickOpenDirectoryEntries, quickOpenFileEntries, quickOpenQuery } from './quick-open.js';

function rank(query: string, files: readonly string[], recent: readonly string[] = [], more: readonly string[] = []) {
  return quickOpenFileEntries(query, createQuickOpenFileIndex(recent), createQuickOpenFileIndex(files), createQuickOpenFileIndex(more));
}

it('keeps line ranges separate from the search query', () => {
  expect(quickOpenQuery(' notes:30-33')).toEqual({
    kind: 'search', query: 'notes', target: { path: 'notes', line: 30, endLine: 33 },
  });
});

it.each([['/tmp/', '/tmp'], ['~/', '~'], ['./', '.'], ['../../', '../..'], ['/', '/']])('splits a directory query under %s', (input, directory) => {
  expect(quickOpenQuery(input + 'log#L17')).toEqual({
    kind: 'directory', directory, prefix: 'log', target: { path: input + 'log', line: 17 },
  });
});

it('matches terms in the directory and filename and returns exact highlights', () => {
  const path = 'fe/web/src/features/report/quick-open/public.tsx';
  const entries = rank('quick pub', ['quick-open/README.md', 'report/public.tsx', path], [], [path]);
  expect(entries).toEqual([{
    path, name: 'public.tsx', directory: 'fe/web/src/features/report/quick-open', group: null, isDirectory: false,
    positions: new Set([27, 28, 29, 30, 31, 38, 39, 40]),
  }]);
});

it('ranks camel case and word boundary matches above interior matches', () => {
  expect(rank('qop', ['src/aqaobpc.ts', 'src/QuickOpenPane.ts']).map(entry => entry.name))
    .toEqual(['QuickOpenPane.ts', 'aqaobpc.ts']);
  for (const separator of ['-', '_', '.', '/']) {
    const boundary = `src/quick${separator}open${separator}pane.ts`;
    expect(rank('qop', ['src/aqaobpc.ts', boundary])[0].path).toBe(boundary);
  }
  expect(rank('QOP', ['src/quickOpenPane.ts', 'src/QuickOpenPane.ts']).map(entry => entry.name))
    .toEqual(['QuickOpenPane.ts']);
});

it('prefers basename matches and uses recency to order close scores', () => {
  expect(rank('open', ['open/x.ts', 'src/open.ts']).map(entry => entry.path)).toEqual(['src/open.ts', 'open/x.ts']);
  expect(rank('open', ['a/open.ts', 'z/open.ts', 'open/long.ts'], ['z/open.ts', 'open/long.ts']).map(entry => entry.path))
    .toEqual(['z/open.ts', 'a/open.ts', 'open/long.ts']);
});

it('still refines basename matches and favorites when the index requires a shortlist', () => {
  const paths = Array.from({ length: 2_010 }, (_, index) => `pkg${index}/quick/notes.ts`);
  const recent = 'archived/QuickNote.ts';
  const exact = 'an/otherwise/long/directory/quick';
  expect(rank('quick', [...paths, exact], [recent]).slice(0, 2).map(entry => entry.path)).toEqual([exact, recent]);
});

it('keeps an exact basename even when 2,000 shorter basename matches can fill the shortlist', () => {
  const paths = Array.from({ length: 2_000 }, (_, index) => `a${index}/quick.ts`);
  const exact = 'an/otherwise/long/directory/quick';
  expect(rank('quick', [...paths, exact])[0].path).toBe(exact);
});

it.each(['public.tsx', '^public.tsx'])('bounds oversized %s buckets and keeps the shortest path', name => {
  const paths = Array.from({ length: 19_999 }, (_, index) => `packages/p${index}/src/components/Settings/${name}`);
  const shortest = `src/${name}`;
  const matches = createQuickOpenFileIndex([...paths, shortest]).find(name);

  expect(matches.length).toBeLessThanOrEqual(1_000);
  expect(matches.some(match => match.item === shortest)).toBe(true);
});

it('returns fzf V2 scores and positions from a warm 20,000-path index', () => {
  const path = 'packages/p199/src/components/TrackSearch/public.tsx';
  const paths = Array.from({ length: 19_999 }, (_, index) => `packages/p${index}/src/components/Settings/public.tsx`);
  const index = createQuickOpenFileIndex([...paths, path]);
  for (const query of ['t', 'track', 'track search']) {
    const matches = index.find(query);
    const items = matches.map(match => match.item);
    expect(items).toContain(query === 't' ? paths[0] : path);
    expect(matches).toEqual(new Fzf(items, { match: extendedMatch, forward: false, sort: false }).find(query));
  }
});

it('gives literal paths priority and maps normalized highlights onto the original spelling', () => {
  const literal = 'draft:notes.txt';
  expect(rank(literal, ['archive/' + literal, literal])[0].path).toBe(literal);
  const file = 'docs/Cafe\u0301.md';
  expect(rank('café', [file])[0].positions).toEqual(new Set([5, 6, 7, 8, 9]));
  expect(rank('notes', ['📁/notes.txt'])[0].positions).toEqual(new Set([3, 4, 5, 6, 7]));
});

it.each(['^notes.md', '!notes.md'])('retains a nested literal basename %s when fzf interprets it as syntax', name => {
  const path = `docs/${name}`;
  expect(rank(name, [], [], [path])[0]).toMatchObject({ path, positions: new Set(
    Array.from({ length: name.length }, (_, index) => 'docs/'.length + index),
  ) });
});

it('groups MRU files before up to 50 unique workspace files for an empty query', () => {
  const paths = Array.from({ length: 65 }, (_, index) => `path${index}.md`);
  const recent = ['~/notes.md', 'path2.md', 'path0.md'];
  expect(rank('', paths, recent).slice(0, 4).map(({ group, path }) => [group, path])).toEqual([
    ['Recent', '~/notes.md'], ['Recent', 'path2.md'], ['Recent', 'path0.md'], ['Files', 'path1.md'],
  ]);
  expect(rank('', paths, recent).filter(entry => entry.group === 'Files')).toHaveLength(50);
  expect(rank('', paths)[0]).toMatchObject({ path: 'path0.md', group: 'Files' });
  expect(rank('', paths, recent).filter(entry => entry.path === 'path2.md')).toHaveLength(1);
});

it('fuzzy matches directories and files, preferring exact names and prefixes and preserving the path spelling', () => {
  const entries = [
    { name: 'LOG', is_dir: true }, { name: 'Log server.txt', is_dir: false }, { name: 'catalog', is_dir: false },
  ];
  expect(quickOpenDirectoryEntries('~', 'log', entries).map(entry => [entry.path, entry.isDirectory]))
    .toEqual([['~/LOG', true], ['~/Log server.txt', false], ['~/catalog', false]]);
  expect(quickOpenDirectoryEntries('~', 'lgs', entries)).toEqual([{
    path: '~/Log server.txt', name: 'Log server.txt', directory: '~', group: null, isDirectory: false, positions: new Set([2, 4, 6]),
  }]);
});

it('retains symlink-sensitive parent segments when completing a directory', () => {
  expect(quickOpenQuery('alias-dir/../out')).toEqual({
    kind: 'directory', directory: 'alias-dir/..', prefix: 'out', target: { path: 'alias-dir/../out' },
  });
});

it.each(['//example.com/log', 'https://example.com/../log'])('does not browse URL-shaped text %s', input => {
  expect(quickOpenQuery(input).kind).toBe('search');
});
