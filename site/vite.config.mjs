import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
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

function escapeHtml(value) {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

function renderInlineMarkdown(text) {
  let html = escapeHtml(text);
  html = html.replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_m, label, rawHref) => {
    let href = rawHref;
    if (href === '../../README.md') href = 'https://github.com/keanji-x/perga#readme';
    else if (href === '../../DESIGN.md') href = 'https://github.com/keanji-x/perga/blob/main/DESIGN.md';
    else if (href === 'vision.md') href = 'https://github.com/keanji-x/perga/blob/main/docs/product/vision.md';
    const external = /^https?:\/\//i.test(href) ? ' target="_blank" rel="noreferrer"' : '';
    return `<a href="${href}"${external}>${label}</a>`;
  });
  html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  html = html.replace(/`([^`]+)`/g, '<code>$1</code>');
  return html;
}

function renderVisionMarkdown(markdown) {
  const lines = markdown.split(/\r?\n/);
  const blocks = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i].trim();
    if (!line) {
      i++;
      continue;
    }
    if (line === '---') {
      blocks.push('<hr />');
      i++;
      continue;
    }
    if (line.startsWith('# ')) {
      blocks.push(`<h1>${renderInlineMarkdown(line.slice(2))}</h1>`);
      i++;
      continue;
    }
    if (line.startsWith('## ')) {
      blocks.push(`<h2>${renderInlineMarkdown(line.slice(3))}</h2>`);
      i++;
      continue;
    }
    if (line.startsWith('> ')) {
      const quoteLines = [];
      while (i < lines.length && lines[i].trim().startsWith('> ')) {
        quoteLines.push(lines[i].trim().slice(2));
        i++;
      }
      blocks.push(`<blockquote><p>${renderInlineMarkdown(quoteLines.join(' '))}</p></blockquote>`);
      continue;
    }
    if (/^\d+\.\s+/.test(line)) {
      const items = [];
      while (i < lines.length && /^\d+\.\s+/.test(lines[i].trim())) {
        const parts = [lines[i].trim().replace(/^\d+\.\s+/, '')];
        i++;
        while (i < lines.length && lines[i].trim() && !/^\d+\.\s+/.test(lines[i].trim()) && !/^[#>-]/.test(lines[i].trim())) {
          parts.push(lines[i].trim());
          i++;
        }
        items.push(`<li>${renderInlineMarkdown(parts.join(' '))}</li>`);
        let next = i;
        while (next < lines.length && !lines[next].trim()) next++;
        if (next < lines.length && /^\d+\.\s+/.test(lines[next].trim())) {
          i = next;
        }
      }
      blocks.push(`<ol>${items.join('')}</ol>`);
      continue;
    }
    if (line.startsWith('- ')) {
      const items = [];
      while (i < lines.length && lines[i].trim().startsWith('- ')) {
        items.push(`<li>${renderInlineMarkdown(lines[i].trim().slice(2))}</li>`);
        i++;
      }
      blocks.push(`<ul>${items.join('')}</ul>`);
      continue;
    }
    const para = [];
    while (i < lines.length && lines[i].trim() && !/^(#{1,3}\s|> |- |\d+\.\s|---$)/.test(lines[i].trim())) {
      para.push(lines[i].trim());
      i++;
    }
    blocks.push(`<p>${renderInlineMarkdown(para.join(' '))}</p>`);
  }

  return blocks.join('\n');
}

function buildVisionPageHtml(markdown, markSvg) {
  const bodyHtml = renderVisionMarkdown(markdown);
  const inlineMark = markSvg.replace('<svg ', '<svg class="mark" aria-hidden="true" ');
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Vision — Perga</title>
  <meta name="description" content="Perga Vision and Fundamental Tensions: organizing multiple AIs for long-horizon work." />
  <link rel="icon" type="image/svg+xml" href="./favicon.svg?v=2" />
  <link rel="icon" type="image/png" sizes="192x192" href="./icon-192.png" />
  <link rel="apple-touch-icon" href="./icon-192.png" />
  <link rel="preconnect" href="https://fonts.googleapis.com" />
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
  <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600&family=JetBrains+Mono:wght@400;500&family=Newsreader:ital,opsz,wght@0,6..72,400;0,6..72,500;0,6..72,600;1,6..72,400&display=swap" rel="stylesheet" />
  <style>
    :root {
      color-scheme: light;
      --bg: #f6f4ee;
      --paper: #fdfcfa;
      --text: #1e2026;
      --text-2: #525763;
      --text-3: #868b98;
      --hairline: #d8d4cb;
      --accent: #3955b8;
      --font-serif: "Newsreader", "Iowan Old Style", Charter, Georgia, serif;
      --font-sans: "Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
      --font-mono: "JetBrains Mono", ui-monospace, SFMono-Regular, Menlo, monospace;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font-family: var(--font-sans);
      font-size: 16px;
      line-height: 1.72;
    }
    a { color: var(--accent); text-decoration: none; }
    a:hover { text-decoration: underline; }
    .frame {
      max-width: 820px;
      margin: 0 auto;
      min-height: 100vh;
      background: var(--paper);
      border-left: 1px solid var(--hairline);
      border-right: 1px solid var(--hairline);
    }
    header {
      position: sticky;
      top: 0;
      background: color-mix(in srgb, var(--paper) 92%, transparent);
      backdrop-filter: blur(10px);
      border-bottom: 1px solid var(--hairline);
      padding: 14px 36px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
    }
    .brand {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      color: var(--text);
      font-family: var(--font-serif);
      font-size: 1.15rem;
      font-weight: 600;
    }
    .brand:hover { text-decoration: none; }
    .mark { width: 22px; height: 22px; display: block; }
    .nav {
      display: flex;
      gap: 18px;
      font-size: 0.86rem;
      color: var(--text-2);
    }
    .nav a { color: var(--text-2); }
    .nav a:hover { color: var(--text); text-decoration: none; }
    article {
      padding: 52px 36px 72px;
    }
    h1 {
      margin: 0 0 24px;
      font-family: var(--font-serif);
      font-size: clamp(2.1rem, 4.5vw, 3rem);
      font-weight: 500;
      line-height: 1.1;
      letter-spacing: -0.025em;
    }
    h2 {
      margin: 40px 0 14px;
      font-family: var(--font-serif);
      font-size: 1.5rem;
      font-weight: 500;
      line-height: 1.25;
      padding-top: 24px;
      border-top: 1px solid var(--hairline);
    }
    p { margin: 0 0 16px; color: var(--text-2); }
    strong { color: var(--text); font-weight: 600; }
    blockquote {
      margin: 0 0 24px;
      padding: 14px 20px;
      border-left: 2.5px solid var(--accent);
      background: var(--bg);
      border-radius: 0 4px 4px 0;
    }
    blockquote p {
      margin: 0;
      font-family: var(--font-serif);
      font-size: 1.12rem;
      line-height: 1.55;
      color: var(--text);
    }
    ol, ul { margin: 0 0 20px; padding-left: 1.35rem; color: var(--text-2); }
    li { margin-bottom: 12px; }
    code {
      font-family: var(--font-mono);
      font-size: 0.88em;
      background: var(--bg);
      padding: 1px 5px;
      border-radius: 3px;
    }
    hr { border: none; border-top: 1px solid var(--hairline); margin: 36px 0 20px; }
    @media (max-width: 680px) {
      .frame { border-left: none; border-right: none; }
      header, article { padding-left: 20px; padding-right: 20px; }
    }
  </style>
</head>
<body>
  <div class="frame">
    <header>
      <a class="brand" href="./">${inlineMark}<span>Perga</span></a>
      <nav class="nav">
        <a href="./">← Home</a>
        <a href="https://github.com/keanji-x/perga/blob/main/docs/product/vision.md" target="_blank" rel="noreferrer">中文 ↗</a>
        <a href="https://github.com/keanji-x/perga" target="_blank" rel="noreferrer">GitHub ↗</a>
      </nav>
    </header>
    <article>
      ${bodyHtml}
    </article>
  </div>
</body>
</html>
`;
}

function copyCanonicalAssetsAndBuildVision() {
  const assetCopies = [
    ['templates/equity-research/widgets/range-axis.html', 'widgets/range-axis.html'],
    ['src/Perga.Kernel/Templates/Memory/widgets/entries.html', 'widgets/entries.html'],
    ['fe/web/src/ui/brand/perga-app.svg', 'favicon.svg'],
    ['fe/web/public/icons/perga-192.png', 'icon-192.png'],
  ];

  return {
    name: 'perga-copy-canonical-assets',
    closeBundle() {
      const outDir = resolve(siteDir, 'dist');
      for (const [srcRel, dstRel] of assetCopies) {
        const dst = resolve(outDir, dstRel);
        mkdirSync(dirname(dst), { recursive: true });
        copyFileSync(resolve(repoRoot, srcRel), dst);
      }

      const visionMd = readFileSync(resolve(repoRoot, 'docs/product/vision.en.md'), 'utf8');
      const markSvg = readFileSync(resolve(repoRoot, 'fe/web/src/ui/brand/perga-mark.svg'), 'utf8');
      writeFileSync(resolve(outDir, 'vision.html'), buildVisionPageHtml(visionMd, markSvg), 'utf8');
    },
  };
}

export default {
  root: siteDir,
  base: './',
  publicDir: resolve(siteDir, 'public'),
  plugins: [resolveFromFeNodeModules(), react(), copyCanonicalAssetsAndBuildVision()],
  resolve: {
    dedupe: ['react', 'react-dom'],
  },
  build: {
    outDir: resolve(siteDir, 'dist'),
    emptyOutDir: true,
  },
};
