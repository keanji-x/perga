import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

// Canonical brand mark sources. Point `favicon` or `og` to a separate optical variant
// (for example a simplified compact SVG) in the future without touching consumers.
const MARK_SOURCES = {
  app: new URL('../../web/src/ui/brand/perga-mark.svg', import.meta.url),
  favicon: new URL('../../web/src/ui/brand/perga-mark.svg', import.meta.url),
  og: new URL('../../web/src/ui/brand/perga-mark.svg', import.meta.url),
};

const OG_MARK_SLOT = /<!-- perga-mark:start -->[\s\S]*?<!-- perga-mark:end -->/;

/**
 * @param {string} rawSvg
 * @param {{ x: number, y: number, width: number, height: number }} box
 */
function embedBrandMark(rawSvg, { x, y, width, height }) {
  const viewBox = /\bviewBox="([^"]+)"/.exec(rawSvg)?.[1];
  const group = /(<g\b[\s\S]*?<\/g>)/.exec(rawSvg)?.[1];
  if (viewBox === undefined || group === undefined) {
    throw new Error('Canonical brand mark SVG must contain a viewBox and a <g> element.');
  }
  const indentedGroup = group
    .split('\n')
    .map((line) => `    ${line.trim()}`)
    .join('\n');
  return `<svg x="${x}" y="${y}" width="${width}" height="${height}" viewBox="${viewBox}">\n${indentedGroup}\n  </svg>`;
}

/**
 * @param {string} rawSvg
 */
function composeAppIcon(rawSvg) {
  const inset = 48;
  const innerSize = 512 - inset * 2;
  const embedded = rawSvg
    .trim()
    .replace('<svg ', `<svg x="${inset}" y="${inset}" width="${innerSize}" height="${innerSize}" `);
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label="Perga">
  <rect x="2" y="2" width="508" height="508" rx="116" fill="#EFECE3" stroke="#D8D2C5" stroke-width="4" />
  ${embedded}
</svg>
`;
}

const [appMarkRaw, faviconMarkRaw, ogMarkRaw] = await Promise.all([
  readFile(MARK_SOURCES.app, 'utf8'),
  readFile(MARK_SOURCES.favicon, 'utf8'),
  readFile(MARK_SOURCES.og, 'utf8'),
]);

const appIcon = composeAppIcon(appMarkRaw);
const faviconIcon = composeAppIcon(faviconMarkRaw);

const output = new URL('../../web/public/icons/', import.meta.url);
await mkdir(output, { recursive: true });
await writeFile(new URL('../../web/src/ui/brand/perga-app.svg', import.meta.url), appIcon);
await writeFile(new URL('../../../site/public/favicon.svg', import.meta.url), faviconIcon);

const ogSvgUrl = new URL('../../../site/public/og-card.svg', import.meta.url);
const existingOgSvg = await readFile(ogSvgUrl, 'utf8');
if (!OG_MARK_SLOT.test(existingOgSvg)) {
  throw new Error('site/public/og-card.svg is missing <!-- perga-mark:start --> ... <!-- perga-mark:end --> markers.');
}
const syncedOgSvg = existingOgSvg.replace(
  OG_MARK_SLOT,
  `<!-- perga-mark:start -->\n  ${embedBrandMark(ogMarkRaw, { x: 94, y: 40, width: 36, height: 36 })}\n  <!-- perga-mark:end -->`,
);
await writeFile(ogSvgUrl, syncedOgSvg);

const browser = await chromium.launch();
try {
  for (const size of [192, 512]) {
    const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
    await page.setContent(`<style>body { margin: 0; } body > svg { width: 100vw; height: 100vh; display: block; }</style>${appIcon}`);
    await page.screenshot({ path: fileURLToPath(new URL(`perga-${size}.png`, output)), omitBackground: true });
    await page.close();
  }

  const ogPage = await browser.newPage({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1 });
  await ogPage.setContent(`<style>body { margin: 0; } body > svg { width: 1200px; height: 630px; display: block; }</style>${syncedOgSvg}`);
  await ogPage.screenshot({ path: fileURLToPath(new URL('../../../site/public/og-card.png', import.meta.url)) });
  await ogPage.close();
} finally {
  await browser.close();
}
