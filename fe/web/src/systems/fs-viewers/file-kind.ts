import { isHtmlPagePath } from '../../../../core/domain/workspace-html.ts';

const IMAGE_EXTENSIONS = Object.freeze([
  '.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.ico', '.svg',
] as const);

export function isImagePath(path: string): boolean {
  const lower = path.toLowerCase();
  return IMAGE_EXTENSIONS.some((extension) => lower.endsWith(extension));
}

export function isMarkdownPath(path: string): boolean {
  const lower = path.toLowerCase();
  return lower.endsWith('.md') || lower.endsWith('.markdown');
}

/** HTML pages are shown as source in the file viewer. */
export function isHtmlPath(path: string): boolean {
  return isHtmlPagePath(path);
}
