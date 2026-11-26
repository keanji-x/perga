import { describe, expect, it } from 'vitest';
import {
  formatFileLine, parseFileLine, parseFileOpenInput, isFilePath, parseReportFileLink,
  reportFileNavigationPath,
} from './report-file.js';

describe('file links', () => {
  it.each([
    ['./src/../README%20first.md#L12', { path: 'src/../README first.md', line: 12 }],
    ['fe/web/src/main.tsx:42-60', { path: 'fe/web/src/main.tsx', line: 42, endLine: 60 }],
    ['file:///srv/work/src/main.rs:42:7', { path: '/srv/work/src/main.rs', line: 42 }],
    ['~/notes.md#L4-L6', { path: '~/notes.md', line: 4, endLine: 6 }],
    ['docs/100%2520done.md', { path: 'docs/100%20done.md' }],
    ['src/../../manual.txt', { path: 'src/../../manual.txt' }],
    ['/tmp/log.txt', { path: '/tmp/log.txt' }],
    ['./draft%3Anotes.txt', { path: './draft:notes.txt' }],
    ['draft%5Cnotes.txt', { path: 'draft\\notes.txt' }],
  ])('normalizes %s and preserves its line location', (spelling, expected) => {
    expect(parseReportFileLink(spelling)).toEqual(expected);
  });

  it.each(['', '.', 'https://example.com/x', '//example.com/x', '#section', '?download=1', 'file://hostname/x',
    'perga://wave/w1', 'other:notes.txt', 'javascript:alert(1)', 'javascript:42', 'bad%ZZ.txt', 'bad%00.txt', 'bad\\path.txt',
  ])('rejects the Markdown destination %j', (destination) => {
    expect(parseReportFileLink(destination)).toBeNull();
  });

  it('keeps decoded characters literal in paths sent through the route or MRU', () => {
    expect(isFilePath('docs/100%20done.md')).toBe(true);
    expect(isFilePath('/tmp/a#L5')).toBe(true);
    expect(isFilePath('../a/../b')).toBe(true);
    expect(isFilePath('draft:notes.txt')).toBe(true);
    expect(isFilePath('draft\\notes.txt')).toBe(true);
    expect(isFilePath('./~/notes.txt')).toBe(true);
    expect(parseFileOpenInput('alias-dir/../outside.txt:42')).toEqual({ path: 'alias-dir/../outside.txt', line: 42 });
    expect(parseFileOpenInput('README:42')).toEqual({ path: 'README', line: 42 });
    expect(parseFileOpenInput('/tmp/a%20b:5')).toEqual({ path: '/tmp/a%20b', line: 5 });
    expect(parseFileOpenInput('draft:notes.txt')).toEqual({ path: 'draft:notes.txt' });
    expect(parseFileOpenInput('draft\\notes.txt')).toEqual({ path: 'draft\\notes.txt' });
    expect(parseFileOpenInput('javascript:42')).toBeNull();
  });

  it('formats one-based lines and inclusive ranges', () => {
    expect(parseFileLine(42)).toEqual({ line: 42 });
    expect(parseFileLine('42-60')).toEqual({ line: 42, endLine: 60 });
    expect(formatFileLine({ line: 42, endLine: 60 })).toBe('42-60');
    for (const invalid of ['0', '-1', '3-2', '9007199254740992', ['1', '2']]) {
      expect(parseFileLine(invalid)).toEqual({});
    }
  });
});

describe('file navigation paths', () => {
  it('resolves siblings and parents against the parent document', () => {
    expect(reportFileNavigationPath('/repo', './spec.md', 'docs')).toBe('docs/./spec.md');
    expect(reportFileNavigationPath('/repo', '../README.md', 'docs')).toBe('docs/../README.md');
    expect(reportFileNavigationPath('/repo', '../README.md')).toBe('../README.md');
    expect(reportFileNavigationPath('/repo', './notes.md', '/tmp')).toBe('/tmp/./notes.md');
  });

  it('shortens workspace absolute paths and preserves external and home paths', () => {
    expect(reportFileNavigationPath('/srv/work/', '/srv/work/docs/100%20done.md')).toBe('docs/100%20done.md');
    expect(reportFileNavigationPath('/', '/tmp/a.txt')).toBe('tmp/a.txt');
    expect(reportFileNavigationPath('/repo', '/repo-other/a.txt')).toBe('/repo-other/a.txt');
    expect(reportFileNavigationPath('/repo', '~/notes.md')).toBe('~/notes.md');
  });

  it('escapes literal tilde components when shortening workspace paths', () => {
    expect(reportFileNavigationPath('/repo', '/repo/~/picture.png')).toBe('./~/picture.png');
    expect(reportFileNavigationPath('/repo', './~/picture.png')).toBe('./~/picture.png');
  });
});
