import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

// Compose the app icon from the single canonical mark on Perga's warm parchment ground (#EFECE3).
const mark = await readFile(new URL('../../web/src/ui/brand/perga-mark.svg', import.meta.url), 'utf8');
const inset = 48;
const innerSize = 512 - inset * 2;
const embedded = mark
  .trim()
  .replace('<svg ', `<svg x="${inset}" y="${inset}" width="${innerSize}" height="${innerSize}" `);
const icon = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label="Perga">
  <rect x="2" y="2" width="508" height="508" rx="116" fill="#EFECE3" stroke="#D8D2C5" stroke-width="4" />
  ${embedded}
</svg>
`;

const output = new URL('../../web/public/icons/', import.meta.url);
await mkdir(output, { recursive: true });
await writeFile(new URL('../../web/src/ui/brand/perga-app.svg', import.meta.url), icon);
await writeFile(new URL('../../../site/public/favicon.svg', import.meta.url), icon);

const ogSvg = await readFile(new URL('../../../site/public/og-card.svg', import.meta.url), 'utf8');

const browser = await chromium.launch();
try {
  for (const size of [192, 512]) {
    const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
    await page.setContent(`<style>body { margin: 0; } body > svg { width: 100vw; height: 100vh; display: block; }</style>${icon}`);
    await page.screenshot({ path: fileURLToPath(new URL(`perga-${size}.png`, output)), omitBackground: true });
    await page.close();
  }

  const ogPage = await browser.newPage({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1 });
  await ogPage.setContent(`<style>body { margin: 0; } body > svg { width: 1200px; height: 630px; display: block; }</style>${ogSvg}`);
  await ogPage.screenshot({ path: fileURLToPath(new URL('../../../site/public/og-card.png', import.meta.url)) });
  await ogPage.close();
} finally {
  await browser.close();
}
