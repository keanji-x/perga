import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { ResolvedConfig } from 'vite';
import { afterEach, describe, expect, it } from 'vitest';

import { EXPECTED_LAYER_ORDER } from './repository-check.mjs';
import {
  auditBuiltCascadeOrder,
  cascadeLayerOrderPlugin,
  extractEffectiveLayerOrder,
  extractHtmlStylesheetSources,
  prependCascadeLayerOrder,
} from './vite-cascade-order.ts';

const tempDirs: string[] = [];

function makeDist(files: Readonly<Record<string, string>>): string {
  const dir = mkdtempSync(join(tmpdir(), 'perga-cascade-order-'));
  tempDirs.push(dir);
  for (const [relPath, content] of Object.entries(files)) {
    const full = join(dir, relPath);
    mkdirSync(join(full, '..'), { recursive: true });
    writeFileSync(full, content, 'utf8');
  }
  return dir;
}

afterEach(() => {
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop();
    if (dir !== undefined) rmSync(dir, { recursive: true, force: true });
  }
});

describe('prependCascadeLayerOrder', () => {
  it('prepends the @layer statement to plain minified CSS', () => {
    const result = prependCascadeLayerOrder('@layer ui{.a{color:red}}', EXPECTED_LAYER_ORDER);
    expect(result).toBe(`@layer ${EXPECTED_LAYER_ORDER.join(',')};@layer ui{.a{color:red}}`);
  });

  it('preserves a leading UTF-8 BOM without @charset so the first rule stays valid', () => {
    const result = prependCascadeLayerOrder('\uFEFF@layer ui{.a{color:red}}', ['reset', 'ui']);
    expect(result).toBe('\uFEFF@layer reset,ui;@layer ui{.a{color:red}}');
    expect(extractEffectiveLayerOrder([result])).toEqual(['reset', 'ui']);
  });

  it('preserves a leading double-quoted or single-quoted @charset (with or without BOM)', () => {
    expect(prependCascadeLayerOrder('@charset "UTF-8";@layer reset{body{margin:0}}', ['reset', 'ui']))
      .toBe('@charset "UTF-8";@layer reset,ui;@layer reset{body{margin:0}}');
    expect(prependCascadeLayerOrder("\uFEFF@charset 'UTF-8';@layer reset{body{margin:0}}", ['reset', 'ui']))
      .toBe("\uFEFF@charset 'UTF-8';@layer reset,ui;@layer reset{body{margin:0}}");
  });

  it('does not duplicate an identical leading @layer statement', () => {
    const css = `@layer ${EXPECTED_LAYER_ORDER.join(',')};@layer reset{body{margin:0}}`;
    expect(prependCascadeLayerOrder(css, EXPECTED_LAYER_ORDER)).toBe(css);
  });

  it('prepends the authoritative order when a different @layer statement leads', () => {
    const result = prependCascadeLayerOrder('@layer ui,reset;@layer ui{}', ['reset', 'ui']);
    expect(result).toBe('@layer reset,ui;@layer ui,reset;@layer ui{}');
  });
});

describe('extractHtmlStylesheetSources', () => {
  it('collects stylesheet links and inline styles in document order across quote styles and ignores non-stylesheet/script/template markup', () => {
    const html = [
      '<!doctype html><html><head>',
      '<!-- <link rel="stylesheet" href="/assets/commented.css"> -->',
      '<link rel="icon" href="/icon.svg" />',
      '<link rel="modulepreload" href="/assets/app.js">',
      '<link rel="alternate stylesheet" href="/assets/alt.css">',
      '<link title="a > b" href="/assets/first.css" rel="stylesheet" crossorigin>',
      '<script>const s = "<style>@layer ui{}</style><link rel=stylesheet href=/assets/fake.css>";</script>',
      '<template><style>@layer ui{}</style></template>',
      '<style>@layer tokens{}</style>',
      "<link rel='stylesheet' href='/assets/second.css'>",
      '<link rel=stylesheet href=/assets/third.css>',
      '</head></html>',
    ].join('');
    expect(extractHtmlStylesheetSources(html)).toEqual([
      { kind: 'link', href: '/assets/first.css' },
      { kind: 'inline', css: '@layer tokens{}' },
      { kind: 'link', href: '/assets/second.css' },
      { kind: 'link', href: '/assets/third.css' },
    ]);
  });
});

describe('extractEffectiveLayerOrder', () => {
  it('records first-come top-level registration across statements, blocks, @import layer(), and @media wrappers', () => {
    const sheets = [
      '@import "./a.css" layer(reset); @media screen { @layer vendor.sub { .x {} } }',
      '@layer tokens, base; @layer astryx { @layer astryx-base { .btn {} } }',
      '@layer ui.card { .c {} } @layer features { @layer components { .p {} } } @layer overrides {}',
    ];
    expect(extractEffectiveLayerOrder(sheets)).toEqual([...EXPECTED_LAYER_ORDER]);
  });

  it('does not treat the word layer inside a quoted @import URL as a layer modifier', () => {
    expect(extractEffectiveLayerOrder(['@import "./my layer .css"; @layer reset {}'])).toEqual(['reset']);
  });

  it('surfaces anonymous top-level @layer blocks, anonymous @import layer, and invalid comma-separated @layer blocks', () => {
    expect(extractEffectiveLayerOrder(['@layer { .x {} } @layer reset {}'])).toEqual(['<anonymous>', 'reset']);
    expect(extractEffectiveLayerOrder(['@import "./x.css" layer; @layer reset {}'])).toEqual(['<anonymous>', 'reset']);
    expect(extractEffectiveLayerOrder(['@layer reset, ui { .x {} }'])).toEqual(['<invalid-layer-list>']);
  });
});

describe('auditBuiltCascadeOrder', () => {
  it('accepts a single-bundle build whose effective layer order matches entry.css', () => {
    const dir = makeDist({
      'index.html': '<!doctype html><html><head><link rel="stylesheet" href="/assets/style-abc.css?v=1#top"></head></html>',
      'assets/style-abc.css': `@layer ${EXPECTED_LAYER_ORDER.join(',')};@layer ui{.x{}}@layer reset{button{padding:0}}`,
    });
    expect(auditBuiltCascadeOrder(dir, EXPECTED_LAYER_ORDER)).toEqual([]);
  });

  it('accepts valid filenames starting with .. and non-root base paths', () => {
    const dotPrefixed = makeDist({
      'index.html': '<link rel="stylesheet" href="/assets/..style.css">',
      'assets/..style.css': `@layer ${EXPECTED_LAYER_ORDER.join(',')};`,
    });
    expect(auditBuiltCascadeOrder(dotPrefixed, EXPECTED_LAYER_ORDER)).toEqual([]);

    const prefixedBase = makeDist({
      'index.html': '<link rel="stylesheet" href="/app/assets/style.css">',
      'assets/style.css': `@layer ${EXPECTED_LAYER_ORDER.join(',')};`,
    });
    expect(auditBuiltCascadeOrder(prefixedBase, EXPECTED_LAYER_ORDER, '/app/')).toEqual([]);
  });

  it('accepts multi-stylesheet builds when the leading stylesheet declares the full layer order', () => {
    const dir = makeDist({
      'index.html': [
        '<link rel="stylesheet" href="/assets/chunk.css">',
        '<link rel="stylesheet" href="/assets/index.css">',
      ].join(''),
      'assets/chunk.css': `@layer ${EXPECTED_LAYER_ORDER.join(',')};@layer ui{.x{}}`,
      'assets/index.css': '@layer reset{button{padding:0}}@layer features{.f{}}',
    });
    expect(auditBuiltCascadeOrder(dir, EXPECTED_LAYER_ORDER)).toEqual([]);
  });

  it('rejects the PR #197 regression where a split @layer ui chunk stylesheet precedes entry CSS', () => {
    const dir = makeDist({
      'index.html': [
        '<link rel="stylesheet" crossorigin href="/assets/clipboard-CS-zuQne.css">',
        '<link rel="stylesheet" crossorigin href="/assets/index-CAOCg4u6.css">',
      ].join(''),
      'assets/clipboard-CS-zuQne.css': '@layer ui{._dot{display:inline-block}}',
      'assets/index-CAOCg4u6.css': [
        '@layer reset{button{padding:0}}',
        '@layer vendor{}',
        '@layer tokens{}',
        '@layer base{h1{font-size:inherit}}',
        '@layer astryx{@layer astryx-base{}}',
        '@layer ui{._content{padding-inline-start:28px}}',
        '@layer features{}',
        '@layer overrides{}',
      ].join(''),
    });
    const violations = auditBuiltCascadeOrder(dir, EXPECTED_LAYER_ORDER);
    expect(violations).toHaveLength(1);
    expect(violations[0]).toContain('got [ui, reset, vendor, tokens, base, astryx, features, overrides]');
  });

  it('rejects an inline <style> tag that registers a layer before the linked bundle', () => {
    const dir = makeDist({
      'index.html': '<style>@layer features{}</style><link rel="stylesheet" href="/assets/style.css">',
      'assets/style.css': `@layer ${EXPECTED_LAYER_ORDER.join(',')};`,
    });
    const violations = auditBuiltCascadeOrder(dir, EXPECTED_LAYER_ORDER);
    expect(violations).toHaveLength(1);
    expect(violations[0]).toContain('got [features, reset, vendor, tokens, base, astryx, ui, overrides]');
  });

  it('rejects subset, extra/unknown, anonymous, and invalid comma-list layer outcomes', () => {
    const subset = makeDist({
      'index.html': '<link rel="stylesheet" href="/assets/style.css">',
      'assets/style.css': '@layer reset, vendor, tokens;',
    });
    expect(auditBuiltCascadeOrder(subset, EXPECTED_LAYER_ORDER)[0]).toContain('got [reset, vendor, tokens]');

    const extra = makeDist({
      'index.html': '<link rel="stylesheet" href="/assets/style.css">',
      'assets/style.css': `@layer ${EXPECTED_LAYER_ORDER.join(',')}, alien;`,
    });
    expect(auditBuiltCascadeOrder(extra, EXPECTED_LAYER_ORDER)[0]).toContain('alien');

    const anonymous = makeDist({
      'index.html': '<link rel="stylesheet" href="/assets/style.css">',
      'assets/style.css': `@layer { .x {} } @layer ${EXPECTED_LAYER_ORDER.join(',')};`,
    });
    expect(auditBuiltCascadeOrder(anonymous, EXPECTED_LAYER_ORDER)[0]).toContain('<anonymous>');

    const invalidListBlock = makeDist({
      'index.html': '<link rel="stylesheet" href="/assets/style.css">',
      'assets/style.css': '@layer reset, ui { .x {} }',
    });
    expect(auditBuiltCascadeOrder(invalidListBlock, EXPECTED_LAYER_ORDER)[0]).toContain('<invalid-layer-list>');
  });

  it('rejects missing index.html, empty stylesheet list, missing CSS files, out-of-base links, traversal, root href, and remote URLs', () => {
    const emptyDir = makeDist({});
    expect(auditBuiltCascadeOrder(emptyDir, EXPECTED_LAYER_ORDER)[0]).toContain('index.html not found');

    const noCssDir = makeDist({ 'index.html': '<!doctype html><html><head></head></html>' });
    expect(auditBuiltCascadeOrder(noCssDir, EXPECTED_LAYER_ORDER)[0]).toContain('no active stylesheet');

    const missingAssetDir = makeDist({ 'index.html': '<link rel="stylesheet" href="/assets/missing.css">' });
    expect(auditBuiltCascadeOrder(missingAssetDir, EXPECTED_LAYER_ORDER)[0]).toContain('missing from built output');

    const externalHttps = makeDist({ 'index.html': '<link rel="stylesheet" href="https://cdn.example.com/a.css">' });
    expect(auditBuiltCascadeOrder(externalHttps, EXPECTED_LAYER_ORDER)[0]).toContain('non-local stylesheet href');

    const protocolRelative = makeDist({ 'index.html': '<link rel="stylesheet" href="//cdn.example.com/a.css">' });
    expect(auditBuiltCascadeOrder(protocolRelative, EXPECTED_LAYER_ORDER)[0]).toContain('non-local stylesheet href');

    const outOfBase = makeDist({
      'index.html': '<link rel="stylesheet" href="/assets/style.css">',
      'assets/style.css': `@layer ${EXPECTED_LAYER_ORDER.join(',')};`,
    });
    expect(auditBuiltCascadeOrder(outOfBase, EXPECTED_LAYER_ORDER, '/app/')[0]).toContain('non-local stylesheet href');

    const traversal = makeDist({ 'index.html': '<link rel="stylesheet" href="../outside.css">' });
    expect(auditBuiltCascadeOrder(traversal, EXPECTED_LAYER_ORDER)[0]).toContain('non-local stylesheet href');

    const rootHref = makeDist({ 'index.html': '<link rel="stylesheet" href="/">' });
    expect(auditBuiltCascadeOrder(rootHref, EXPECTED_LAYER_ORDER)[0]).toContain('non-local stylesheet href');
  });
});

describe('cascadeLayerOrderPlugin', () => {
  function runHook<Args extends unknown[]>(hook: unknown, ...args: Args): void {
    const fn = typeof hook === 'function'
      ? (hook as (...a: Args) => void)
      : (hook as { handler: (...a: Args) => void }).handler;
    fn(...args);
  }

  it('prepends the authoritative layer order to string and Uint8Array CSS assets, leaves JS untouched, and validates writeBundle', () => {
    const workspace = makeDist({
      'src/styles/entry.css': '@layer reset, ui, features;\n',
      'dist/index.html': '<link rel="stylesheet" href="/assets/style.css">',
      'dist/assets/style.css': '@layer reset,ui,features;@layer ui{.a{}}',
    });
    const plugin = cascadeLayerOrderPlugin(join(workspace, 'src/styles/entry.css'));
    expect(plugin.apply).toBe('build');
    expect(plugin.enforce).toBe('post');

    runHook(plugin.configResolved, {
      root: workspace,
      base: '/',
      build: { outDir: 'dist' },
    } as ResolvedConfig);
    runHook(plugin.buildStart, {});

    const bundle: Record<string, { type: string; source?: string | Uint8Array; code?: string }> = {
      'assets/style.css': { type: 'asset', source: '@layer ui{.a{}}' },
      'assets/binary.css': { type: 'asset', source: Buffer.from('@layer features{.b{}}', 'utf8') },
      'assets/app.js': { type: 'chunk', code: 'console.log(1);' },
      'assets/icon.svg': { type: 'asset', source: '<svg/>' },
    };
    runHook(plugin.generateBundle, {}, bundle);

    expect(bundle['assets/style.css'].source).toBe('@layer reset,ui,features;@layer ui{.a{}}');
    expect(bundle['assets/binary.css'].source).toBe('@layer reset,ui,features;@layer features{.b{}}');
    expect(bundle['assets/app.js'].code).toBe('console.log(1);');
    expect(bundle['assets/icon.svg'].source).toBe('<svg/>');

    expect(() => { runHook(plugin.writeBundle, {}, bundle); }).not.toThrow();

    // Mutate dist CSS to an inverted order and confirm writeBundle fails the build.
    writeFileSync(join(workspace, 'dist/assets/style.css'), '@layer ui{.a{}}@layer reset{}@layer features{}', 'utf8');
    expect(() => { runHook(plugin.writeBundle, {}, bundle); }).toThrow(/Production CSS cascade layer audit failed/);
  });
});
