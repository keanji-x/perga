import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { isAbsolute, relative, resolve, sep } from 'node:path';
import postcss, { type ChildNode } from 'postcss';
import type { Plugin } from 'vite';

import { layerOrder } from './audit.ts';

const LEADING_PREFIX_RE = /^(?:\uFEFF)?(?:\s*@charset\s+(?:"[^"]*"|'[^']*')\s*;)?/i;
const IMPORT_TARGET_RE = /^\s*(?:url\(\s*(?:"[^"]*"|'[^']*'|[^)]*)\s*\)|"[^"]*"|'[^']*')/i;

type JsdomConstructor = new (html: string) => {
  window: {
    document: {
      querySelectorAll(selector: string): ArrayLike<{
        tagName: string;
        textContent: string | null;
        getAttribute(name: string): string | null;
      }>;
    };
  };
};

const jsdomRequire = createRequire(import.meta.url);
const { JSDOM } = jsdomRequire('jsdom') as { JSDOM: JsdomConstructor };

export type HtmlStylesheetSource =
  | Readonly<{ kind: 'link'; href: string }>
  | Readonly<{ kind: 'inline'; css: string }>;

/**
 * Ensures a compiled CSS asset begins with the authoritative `@layer` order
 * statement (preserving any leading UTF-8 BOM and `@charset`), preventing
 * LightningCSS elision or chunk concatenation order from inverting cascade
 * precedence.
 */
export function prependCascadeLayerOrder(css: string, order: readonly string[]): string {
  if (order.length === 0) return css;
  const statement = `@layer ${order.join(',')};`;
  const prefixMatch = LEADING_PREFIX_RE.exec(css);
  const insertAt = prefixMatch ? prefixMatch[0].length : 0;
  const prefix = css.slice(0, insertAt);
  const rest = css.slice(insertAt);
  const existing = /^(\s*)@layer\s+([^;{}]+);/i.exec(rest);
  if (existing) {
    const names = existing[2].split(',').map((item) => item.trim()).filter(Boolean);
    if (names.length === order.length && names.every((name, index) => name === order[index])) {
      return css;
    }
  }
  return `${prefix}${statement}${rest}`;
}

/**
 * Extracts active `<link rel="stylesheet">` and inline `<style>` elements from
 * built HTML in exact document order using an HTML5 parser (ignoring comments,
 * `<script>` bodies, `<template>` contents, and alternate stylesheets).
 */
export function extractHtmlStylesheetSources(html: string): HtmlStylesheetSource[] {
  const { document } = new JSDOM(html).window;
  const sources: HtmlStylesheetSource[] = [];
  for (const element of Array.from(document.querySelectorAll('link, style'))) {
    if (element.tagName === 'STYLE') {
      sources.push({ kind: 'inline', css: element.textContent ?? '' });
      continue;
    }
    const relTokens = (element.getAttribute('rel') ?? '').toLowerCase().split(/\s+/).filter(Boolean);
    if (!relTokens.includes('stylesheet') || relTokens.includes('alternate')) continue;
    const href = (element.getAttribute('href') ?? '').trim();
    if (href.length > 0) sources.push({ kind: 'link', href });
  }
  return sources;
}

function registerLayerName(target: string[], seen: Set<string>, rawName: string): void {
  const topLevel = rawName.trim().split('.')[0]?.trim() ?? '';
  const name = topLevel.length > 0 ? topLevel : '<anonymous>';
  if (seen.has(name)) return;
  seen.add(name);
  target.push(name);
}

function collectTopLevelLayers(nodes: readonly ChildNode[], target: string[], seen: Set<string>): void {
  for (const node of nodes) {
    if (node.type !== 'atrule') continue;
    const atName = node.name.toLowerCase();
    if (atName === 'layer') {
      const trimmed = node.params.trim();
      if (node.nodes !== undefined) {
        // CSS Cascade 5 forbids comma-separated layer lists on `@layer` blocks.
        if (trimmed.includes(',')) {
          registerLayerName(target, seen, '<invalid-layer-list>');
        } else {
          registerLayerName(target, seen, trimmed);
        }
      } else if (trimmed.length === 0) {
        registerLayerName(target, seen, '');
      } else {
        for (const item of trimmed.split(',')) registerLayerName(target, seen, item);
      }
      // Nested `@layer` blocks define sublayers of the outer layer, never top-level layers.
      continue;
    }
    if (atName === 'import') {
      const modifiers = node.params.replace(IMPORT_TARGET_RE, '');
      const layerMatch = /\blayer(?:\(\s*([^\s)]+)\s*\))?(?=\s|;|$)/i.exec(modifiers);
      if (layerMatch) registerLayerName(target, seen, layerMatch[1] ?? '');
      continue;
    }
    if (node.nodes) collectTopLevelLayers(node.nodes, target, seen);
  }
}

/**
 * Computes the effective top-level CSS cascade layer registration order across
 * stylesheets loaded in document order (first-come registration per CSS Cascade 5).
 */
export function extractEffectiveLayerOrder(stylesheets: readonly string[]): string[] {
  const order: string[] = [];
  const seen = new Set<string>();
  for (const css of stylesheets) {
    const root = postcss.parse(css);
    collectTopLevelLayers(root.nodes, order, seen);
  }
  return order;
}

function resolveLocalAssetPath(distDir: string, href: string, base: string): string | null {
  if (/^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(href)) return null;
  const cleanHref = href.split(/[?#]/, 1)[0] ?? '';
  const normalizedBase = base.endsWith('/') ? base : `${base}/`;
  let relativeHref = cleanHref;
  if (normalizedBase !== '/' && cleanHref.startsWith('/')) {
    if (!cleanHref.startsWith(normalizedBase)) return null;
    relativeHref = cleanHref.slice(normalizedBase.length);
  } else {
    relativeHref = cleanHref.replace(/^\/+/, '');
  }
  const resolvedDist = resolve(distDir);
  const resolvedAsset = resolve(resolvedDist, relativeHref);
  const rel = relative(resolvedDist, resolvedAsset);
  if (rel === '' || rel === '..' || rel.startsWith(`..${sep}`) || isAbsolute(rel)) return null;
  return resolvedAsset;
}

/**
 * Audits a built `dist/` directory's `index.html` and referenced CSS assets to
 * verify that the browser's effective top-level `@layer` registration order
 * matches the authoritative order declared in `web/src/styles/entry.css`.
 *
 * Known static boundaries:
 * - Inspects `<link rel="stylesheet">` and `<style>` tags present in built
 *   `index.html`; runtime JS-injected `<style>` tags or dynamically appended
 *   stylesheets remain covered by runtime browser audits.
 * - Verifies top-level `@layer` registration order only; unlayered selectors and
 *   unbundled `@import` targets remain covered by `auditStyleRepository` /
 *   `auditRuntimeStyles`.
 */
export function auditBuiltCascadeOrder(
  distDir: string,
  expectedOrder: readonly string[],
  base = '/',
): string[] {
  const indexHtmlPath = resolve(distDir, 'index.html');
  if (!existsSync(indexHtmlPath)) {
    return [`built index.html not found at ${indexHtmlPath}`];
  }
  const html = readFileSync(indexHtmlPath, 'utf8');
  const sources = extractHtmlStylesheetSources(html);
  if (sources.length === 0) {
    return ['built index.html contains no active stylesheet or inline style'];
  }

  const violations: string[] = [];
  const stylesheets: string[] = [];
  for (const source of sources) {
    if (source.kind === 'inline') {
      stylesheets.push(source.css);
      continue;
    }
    const assetPath = resolveLocalAssetPath(distDir, source.href, base);
    if (assetPath === null) {
      violations.push(`cannot verify non-local stylesheet href "${source.href}" in built index.html`);
      continue;
    }
    if (!existsSync(assetPath)) {
      violations.push(`stylesheet href "${source.href}" missing from built output (${assetPath})`);
      continue;
    }
    stylesheets.push(readFileSync(assetPath, 'utf8'));
  }
  if (violations.length > 0) return violations;

  const actual = extractEffectiveLayerOrder(stylesheets);
  if (actual.join(',') !== expectedOrder.join(',')) {
    violations.push(
      `built CSS cascade layer order mismatch: expected [${expectedOrder.join(', ')}], got [${actual.join(', ')}]`,
    );
  }
  return violations;
}

/**
 * Vite build plugin that keeps `web/src/styles/entry.css` as the single
 * authoritative source of top-level `@layer` order in production builds:
 * 1. Prepends the `@layer ...;` order statement to emitted CSS assets after
 *    minification so LightningCSS elision or chunk concatenation order cannot
 *    invert cascade precedence.
 * 2. Audits the written `index.html` + CSS assets in `writeBundle` and fails
 *    the build if the effective registration order diverges.
 */
export function cascadeLayerOrderPlugin(entryCssPath: string): Plugin {
  let expectedOrder: readonly string[] = Object.freeze([]);
  let resolvedOutDir = '';
  let resolvedBase = '/';

  return {
    name: 'perga-cascade-layer-order',
    apply: 'build',
    enforce: 'post',
    configResolved(config) {
      resolvedOutDir = resolve(config.root, config.build.outDir);
      resolvedBase = config.base;
    },
    buildStart() {
      expectedOrder = Object.freeze(layerOrder(readFileSync(entryCssPath, 'utf8')));
    },
    generateBundle(_options, bundle) {
      for (const [fileName, output] of Object.entries(bundle)) {
        if (output.type !== 'asset' || !fileName.endsWith('.css')) continue;
        if (typeof output.source === 'string') {
          output.source = prependCascadeLayerOrder(output.source, expectedOrder);
        } else {
          const text = Buffer.from(output.source).toString('utf8');
          output.source = prependCascadeLayerOrder(text, expectedOrder);
        }
      }
    },
    writeBundle() {
      const violations = auditBuiltCascadeOrder(resolvedOutDir, expectedOrder, resolvedBase);
      if (violations.length > 0) {
        throw new Error(`Production CSS cascade layer audit failed:\n${violations.join('\n')}`);
      }
    },
  };
}
