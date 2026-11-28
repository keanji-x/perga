// @vitest-environment node
import { readFileSync } from 'node:fs';
import { PNG } from 'pngjs';
import { expect, it } from 'vitest';

const publicDir = new URL('../../../public/', import.meta.url);

it('declares separate splash and launcher icons in the install manifest', () => {
  const manifest = JSON.parse(readFileSync(new URL('manifest.webmanifest', publicDir), 'utf8')) as unknown;
  expect(manifest).toMatchObject({
    background_color: '#f3f2ee',
    theme_color: '#f3f2ee',
    icons: [
      { src: 'icons/perga-192.png', sizes: '192x192', type: 'image/png', purpose: 'any' },
      { src: 'icons/perga-512.png', sizes: '512x512', type: 'image/png', purpose: 'any' },
      { src: 'icons/perga-maskable-192.png', sizes: '192x192', type: 'image/png', purpose: 'maskable' },
      { src: 'icons/perga-maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
    ],
  });
});

it('serves the same transparent, color-adaptive favicon in the app and on the site', () => {
  const faviconSvg = readFileSync(new URL('./perga-app.svg', import.meta.url), 'utf8');
  const siteFavicon = new URL('../../../../../site/public/favicon.svg', import.meta.url);
  expect(readFileSync(siteFavicon, 'utf8')).toBe(faviconSvg);
  expect(faviconSvg).toMatch(/<svg\b[^>]*viewBox="0 0 384 384"[^>]*role="img"[^>]*aria-label="Perga"/);
  expect(faviconSvg).not.toMatch(/<rect\b|\b(?:stroke|fill)="/);
  const css = /<style>([\s\S]*?)<\/style>/.exec(faviconSvg)?.[1];
  expect(css).toMatch(/g\s*\{\s*fill:\s*#1a1d24\s*;/i);
  expect(css).toMatch(/@media\s*\(prefers-color-scheme:\s*dark\)\s*\{\s*g\s*\{\s*fill:\s*#f3f2ee\s*;/i);
});

it.each([
  { name: 'perga-192.png', size: 192, transparent: true },
  { name: 'perga-512.png', size: 512, transparent: true },
  { name: 'perga-maskable-192.png', size: 192, transparent: false },
  { name: 'perga-maskable-512.png', size: 512, transparent: false },
  { name: 'apple-touch-icon.png', size: 180, transparent: false },
])('renders $name with the required background pixels', ({ name, size, transparent }) => {
  const { width, height, data } = PNG.sync.read(readFileSync(new URL(`icons/${name}`, publicDir)));
  expect([width, height]).toEqual([size, size]);
  const corners = [0, width - 1, (height - 1) * width, width * height - 1];
  const aboveMark = Math.floor(height / 10) * width + Math.floor(width / 2);
  for (const index of [...corners, aboveMark]) {
    const rgba = data.subarray(index * 4, index * 4 + 4);
    if (transparent) {
      expect(rgba[3]).toBe(0);
    } else {
      expect([...rgba]).toEqual([243, 242, 238, 255]);
    }
  }
});
