import { describe, expect, it } from 'vitest';
import { quickOpenQuery } from './quick-open.js';
import {
  collapseBreadcrumbSegments,
  normalizeWorkspaceDisplayPath,
  quickOpenQueryForDirectory,
  splitFileNameTail,
  splitWorkspaceRelativePath,
} from './report-file-layout.js';

describe('normalizeWorkspaceDisplayPath', () => {
  it('shortens paths beneath fileRoot without decoding escapes or collapsing .. segments', () => {
    expect(normalizeWorkspaceDisplayPath('', '/repo')).toBe('');
    expect(normalizeWorkspaceDisplayPath('/repo/docs/README.md', '/repo')).toBe('docs/README.md');
    expect(normalizeWorkspaceDisplayPath('/repo/docs/a%20b.md', '/repo')).toBe('docs/a%20b.md');
    expect(normalizeWorkspaceDisplayPath('/repo/docs/100%.md', '/repo')).toBe('docs/100%.md');
    expect(normalizeWorkspaceDisplayPath('/repo/docs/note:1.md', '/repo')).toBe('docs/note:1.md');
    expect(normalizeWorkspaceDisplayPath('alias-dir/../outside.txt', '/repo')).toBe('alias-dir/../outside.txt');
    expect(normalizeWorkspaceDisplayPath('/repo/~/picture.png', '/repo')).toBe('./~/picture.png');
    expect(normalizeWorkspaceDisplayPath('/var/log/example.log', '/repo')).toBe('/var/log/example.log');
    expect(normalizeWorkspaceDisplayPath('~/debug.log', '/repo')).toBe('~/debug.log');
  });
});

describe('splitWorkspaceRelativePath', () => {
  it('splits nested workspace-relative paths into ancestor segments and filename', () => {
    expect(splitWorkspaceRelativePath('docs/rpc/02_rpc_full_pipeline.md', '/repo')).toEqual({
      normalizedPath: 'docs/rpc/02_rpc_full_pipeline.md',
      fileName: '02_rpc_full_pipeline.md',
      parentDir: 'docs/rpc',
      directorySegments: [
        { name: 'docs', path: 'docs' },
        { name: 'rpc', path: 'docs/rpc' },
      ],
    });
  });

  it('normalizes an absolute path beneath fileRoot to workspace-relative', () => {
    expect(splitWorkspaceRelativePath('/repo/docs/README.md', '/repo')).toEqual({
      normalizedPath: 'docs/README.md',
      fileName: 'README.md',
      parentDir: 'docs',
      directorySegments: [{ name: 'docs', path: 'docs' }],
    });
  });

  it('preserves literal percent signs, colons, ./~ prefix, and symlink-sensitive parent segments', () => {
    expect(splitWorkspaceRelativePath('/repo/docs/a%20b.md', '/repo').normalizedPath).toBe('docs/a%20b.md');
    expect(splitWorkspaceRelativePath('/repo/docs/100%.md', '/repo').normalizedPath).toBe('docs/100%.md');
    expect(splitWorkspaceRelativePath('/repo/docs/note:1.md', '/repo').normalizedPath).toBe('docs/note:1.md');
    expect(splitWorkspaceRelativePath('alias-dir/../outside.txt', '/repo').normalizedPath).toBe('alias-dir/../outside.txt');
    expect(splitWorkspaceRelativePath('/repo/~/picture.png', '/repo')).toEqual({
      normalizedPath: './~/picture.png',
      fileName: 'picture.png',
      parentDir: './~',
      directorySegments: [{ name: '~', path: './~' }],
    });
  });

  it('splits out-of-workspace absolute paths and home-relative paths', () => {
    expect(splitWorkspaceRelativePath('/var/log/example.log', '/repo')).toEqual({
      normalizedPath: '/var/log/example.log',
      fileName: 'example.log',
      parentDir: '/var/log',
      directorySegments: [
        { name: 'var', path: '/var' },
        { name: 'log', path: '/var/log' },
      ],
    });
    expect(splitWorkspaceRelativePath('/example.log', '/repo')).toEqual({
      normalizedPath: '/example.log',
      fileName: 'example.log',
      parentDir: '/',
      directorySegments: [],
    });
    expect(splitWorkspaceRelativePath('~/debug.log', '/repo')).toEqual({
      normalizedPath: '~/debug.log',
      fileName: 'debug.log',
      parentDir: '~',
      directorySegments: [{ name: '~', path: '~' }],
    });
  });

  it('handles root-level files without directory segments', () => {
    expect(splitWorkspaceRelativePath('README.md', '/repo')).toEqual({
      normalizedPath: 'README.md',
      fileName: 'README.md',
      parentDir: '',
      directorySegments: [],
    });
  });
});

describe('collapseBreadcrumbSegments', () => {
  it('does not collapse or invent ellipsis when segments fit within the limit', () => {
    const segments = [
      { name: 'docs', path: 'docs' },
      { name: 'rpc', path: 'docs/rpc' },
    ];
    expect(collapseBreadcrumbSegments(segments, 3)).toEqual({
      leading: segments,
      collapsed: [],
      trailing: [],
    });
  });

  it('collapses middle directories while preserving the root segment and immediate parents', () => {
    const segments = [
      { name: 'docs', path: 'docs' },
      { name: 'architecture', path: 'docs/architecture' },
      { name: 'node', path: 'docs/architecture/node' },
      { name: 'rpc', path: 'docs/architecture/node/rpc' },
    ];
    expect(collapseBreadcrumbSegments(segments, 3)).toEqual({
      leading: [{ name: 'docs', path: 'docs' }],
      collapsed: [{ name: 'architecture', path: 'docs/architecture' }],
      trailing: [
        { name: 'node', path: 'docs/architecture/node' },
        { name: 'rpc', path: 'docs/architecture/node/rpc' },
      ],
    });
  });

  it('clamps maxVisibleDirs below 2 to preserve both the root and immediate parent', () => {
    const segments = [
      { name: 'docs', path: 'docs' },
      { name: 'architecture', path: 'docs/architecture' },
      { name: 'rpc', path: 'docs/architecture/rpc' },
    ];
    expect(collapseBreadcrumbSegments(segments, 1)).toEqual({
      leading: [{ name: 'docs', path: 'docs' }],
      collapsed: [{ name: 'architecture', path: 'docs/architecture' }],
      trailing: [{ name: 'rpc', path: 'docs/architecture/rpc' }],
    });
  });
});

describe('splitFileNameTail', () => {
  it('preserves file extensions for middle-ellipsis rendering across Latin, CJK, and dotfile names', () => {
    expect(splitFileNameTail('02_rpc_full_pipeline_and_node_topology.md')).toEqual({
      stem: '02_rpc_full_pipeline_and_node_topol',
      tail: 'ogy.md',
    });
    expect(splitFileNameTail('README.md')).toEqual({
      stem: 'READM',
      tail: 'E.md',
    });
    expect(splitFileNameTail('中文名字.md')).toEqual({
      stem: '中文名字',
      tail: '.md',
    });
    expect(splitFileNameTail('a.md')).toEqual({
      stem: 'a',
      tail: '.md',
    });
    const deepCjk = splitFileNameTail('02_区块链节点全链路架构梳理.md');
    expect(deepCjk.stem.startsWith('02_')).toBe(true);
    expect(deepCjk.tail.endsWith('.md')).toBe(true);
    expect(`${deepCjk.stem}${deepCjk.tail}`).toBe('02_区块链节点全链路架构梳理.md');
    expect(splitFileNameTail('.gitignore')).toEqual({
      stem: '.gitignore',
      tail: '',
    });
    expect(splitFileNameTail('Makefile')).toEqual({
      stem: 'Makefile',
      tail: '',
    });
  });
});

describe('quickOpenQueryForDirectory', () => {
  it('formats directory queries so non-root folders enter Quick Open literal directory mode', () => {
    expect(quickOpenQueryForDirectory('')).toBe('');
    expect(quickOpenQuery(quickOpenQueryForDirectory('')).kind).toBe('search');

    const cases: readonly [string, string, string][] = [
      ['/', '/', '/'],
      ['docs', './docs/', './docs'],
      ['docs/rpc', './docs/rpc/', './docs/rpc'],
      ['!docs', './!docs/', './!docs'],
      ['alias-dir/..', './alias-dir/../', './alias-dir/..'],
      ['/var/log', '/var/log/', '/var/log'],
      ['~', '~/', '~'],
      ['~/sub', '~/sub/', '~/sub'],
      ['..', '../', '..'],
      ['../home', '../home/', '../home'],
      ['.', './', '.'],
      ['./~', './~/', './~'],
      ['C:/', 'C:/', 'C:/'],
      ['C:/repo', 'C:/repo/', 'C:/repo'],
    ];
    for (const [input, expectedQuery, expectedDirectory] of cases) {
      const formatted = quickOpenQueryForDirectory(input);
      expect(formatted).toBe(expectedQuery);
      expect(quickOpenQuery(formatted)).toMatchObject({
        kind: 'directory',
        directory: expectedDirectory,
        prefix: '',
      });
    }
  });
});
