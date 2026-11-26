import { expect, it } from 'vitest';
import { quickOpenDirectoryEntries, quickOpenQuery } from './quick-open.js';

it('keeps line ranges separate from the server search query', () => {
  expect(quickOpenQuery(' notes:30-33')).toEqual({
    kind: 'search', query: 'notes', target: { path: 'notes', line: 30, endLine: 33 },
  });
});

it.each([['/tmp/', '/tmp'], ['~/', '~'], ['../../', '../..'], ['/', '/']])('splits a directory query under %s', (input, directory) => {
  expect(quickOpenQuery(input + 'log#L17')).toEqual({
    kind: 'directory', directory, prefix: 'log', target: { path: input + 'log', line: 17 },
  });
});

it('matches folder entries by case-insensitive prefix while preserving the target spelling', () => {
  expect(quickOpenDirectoryEntries('~', 'log', [
    { name: 'LOG', is_dir: true }, { name: 'Log server.txt', is_dir: false }, { name: 'catalog', is_dir: false },
  ])).toEqual([{ path: '~/LOG', isDirectory: true }, { path: '~/Log server.txt', isDirectory: false }]);
});

it('retains symlink-sensitive parent segments when completing a directory', () => {
  expect(quickOpenQuery('alias-dir/../out')).toEqual({
    kind: 'directory', directory: 'alias-dir/..', prefix: 'out', target: { path: 'alias-dir/../out' },
  });
});

it.each(['//example.com/log', 'https://example.com/../log'])('does not browse URL-shaped text %s', input => {
  expect(quickOpenQuery(input).kind).toBe('search');
});
