import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const OG_MARK_SLOT = /<!-- perga-mark:start -->[\s\S]*?<!-- perga-mark:end -->/;
const CANVAS_COLOR = '#f3f2ee';

const markSvg = await readFile(new URL('../../web/src/ui/brand/perga-mark.svg', import.meta.url), 'utf8');
const viewBox = /\bviewBox="([^"]+)"/.exec(markSvg)?.[1];
const markGroup = /(<g\b[^>]*\sfill="([^"]+)"[^>]*>[\s\S]*?<\/g>)/.exec(markSvg);
if (viewBox === undefined || markGroup === null) {
  throw new Error('Canonical brand mark SVG must contain a viewBox and a <g> element with a fill.');
}
const [, group, markFill] = markGroup;

/**
 * @param {{ x: number, y: number, size: number }} box
 */
function embedBrandMark({ x, y, size }) {
  const indentedGroup = group
    .split('\n')
    .map((line) => `    ${line.trim()}`)
    .join('\n');
  return `<svg x="${x}" y="${y}" width="${size}" height="${size}" viewBox="${viewBox}">\n${indentedGroup}\n  </svg>`;
}

/**
 * @param {number} inset
 * @param {boolean} opaque
 */
function composeRasterIcon(inset, opaque) {
  const background = opaque
    ? `  <rect x="0" y="0" width="512" height="512" fill="${CANVAS_COLOR}" />\n`
    : '';
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label="Perga">
${background}  ${embedBrandMark({ x: inset, y: inset, size: 512 - inset * 2 })}
</svg>
`;
}

const splashIcon = composeRasterIcon(48, false);
// Keep the mark inside the launcher mask's 80%-diameter safe circle.
const maskableIcon = composeRasterIcon(88, true);
const appleTouchIcon = composeRasterIcon(48, true);
const faviconIcon = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${viewBox}" role="img" aria-label="Perga">
  <style>
    g { fill: ${markFill}; }
    @media (prefers-color-scheme: dark) {
      g { fill: ${CANVAS_COLOR}; }
    }
  </style>
  ${group.replace(` fill="${markFill}"`, '')}
</svg>
`;

const output = new URL('../../web/public/icons/', import.meta.url);
await mkdir(output, { recursive: true });
await writeFile(new URL('../../web/src/ui/brand/perga-app.svg', import.meta.url), faviconIcon);
await writeFile(new URL('../../../site/public/favicon.svg', import.meta.url), faviconIcon);

const ogSvgUrl = new URL('../../../site/public/og-card.svg', import.meta.url);
const existingOgSvg = await readFile(ogSvgUrl, 'utf8');
if (!OG_MARK_SLOT.test(existingOgSvg)) {
  throw new Error('site/public/og-card.svg is missing <!-- perga-mark:start --> ... <!-- perga-mark:end --> markers.');
}
const syncedOgSvg = existingOgSvg.replace(
  OG_MARK_SLOT,
  `<!-- perga-mark:start -->\n  ${embedBrandMark({ x: 94, y: 40, size: 36 })}\n  <!-- perga-mark:end -->`,
);
await writeFile(ogSvgUrl, syncedOgSvg);

const browser = await chromium.launch();
try {
  for (const { name, size, svg } of [
    { name: 'perga-192.png', size: 192, svg: splashIcon },
    { name: 'perga-512.png', size: 512, svg: splashIcon },
    { name: 'perga-maskable-192.png', size: 192, svg: maskableIcon },
    { name: 'perga-maskable-512.png', size: 512, svg: maskableIcon },
    { name: 'apple-touch-icon.png', size: 180, svg: appleTouchIcon },
  ]) {
    const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
    await page.setContent(`<style>body { margin: 0; } body > svg { width: 100vw; height: 100vh; display: block; }</style>${svg}`);
    await page.screenshot({ path: fileURLToPath(new URL(name, output)), omitBackground: true });
    await page.close();
  }

  const ogPage = await browser.newPage({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1 });
  await ogPage.setContent(`<style>body { margin: 0; } body > svg { width: 1200px; height: 630px; display: block; }</style>${syncedOgSvg}`);
  await ogPage.screenshot({ path: fileURLToPath(new URL('../../../site/public/og-card.png', import.meta.url)) });
  await ogPage.close();
} finally {
  await browser.close();
}
