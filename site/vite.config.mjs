import { copyFileSync, mkdirSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const siteDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(siteDir, '..');
const feDir = resolve(repoRoot, 'fe');
const feRequire = createRequire(resolve(feDir, 'package.json'));

const { default: react } = await import(resolve(feDir, 'node_modules/@vitejs/plugin-react/dist/index.js'));

function resolveFromFeNodeModules() {
  return {
    name: 'perga-resolve-from-fe-node-modules',
    enforce: 'pre',
    async resolveId(source, importer, options) {
      if (!source || source.startsWith('.') || source.startsWith('/') || source.startsWith('\0') || source.includes(':')) {
        return null;
      }
      const existing = await this.resolve(source, importer, { ...options, skipSelf: true });
      if (existing) return existing;
      try {
        return feRequire.resolve(source);
      } catch {
        return null;
      }
    },
  };
}

function copyRealPergaWidgets() {
  const widgetCopies = [
    ['templates/equity-research/widgets/range-axis.html', 'widgets/range-axis.html'],
    ['src/Perga.Kernel/Templates/Memory/widgets/entries.html', 'widgets/entries.html'],
  ];

  return {
    name: 'perga-copy-real-widgets',
    closeBundle() {
      const outDir = resolve(siteDir, 'dist');
      for (const [srcRel, dstRel] of widgetCopies) {
        const dst = resolve(outDir, dstRel);
        mkdirSync(dirname(dst), { recursive: true });
        copyFileSync(resolve(repoRoot, srcRel), dst);
      }
    },
  };
}

export default {
  root: siteDir,
  base: './',
  publicDir: resolve(siteDir, 'public'),
  plugins: [resolveFromFeNodeModules(), react(), copyRealPergaWidgets()],
  resolve: {
    dedupe: ['react', 'react-dom'],
  },
  build: {
    outDir: resolve(siteDir, 'dist'),
    emptyOutDir: true,
  },
};
