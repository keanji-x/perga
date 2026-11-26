// Pure workspace-relative path formatting and Quick Open query helpers.
// Keeps Report file layout breadcrumbs platform-independent and deterministic.

import { isAbsoluteFilePath, reportFileNavigationPath } from './report-file.js';

export type BreadcrumbSegment = Readonly<{
  name: string;
  /** Directory path up to this segment (workspace-relative, or absolute for out-of-workspace files). */
  path: string;
}>;

export type WorkspacePathParts = Readonly<{
  /** Normalized workspace-relative path when beneath `fileRoot`, otherwise the cleaned path. */
  normalizedPath: string;
  fileName: string;
  /** Parent directory (`''` for workspace root, `'/'` for filesystem root). */
  parentDir: string;
  directorySegments: readonly BreadcrumbSegment[];
}>;

export type CollapsedBreadcrumb = Readonly<{
  leading: readonly BreadcrumbSegment[];
  collapsed: readonly BreadcrumbSegment[];
  trailing: readonly BreadcrumbSegment[];
}>;

export type FileNameTail = Readonly<{
  /** Leading portion of the filename that may ellipsize when constrained. */
  stem: string;
  /** Trailing filename suffix (extension + a short tail) kept intact against narrow overflow. */
  tail: string;
}>;

function stripTrailingSlashes(path: string): string {
  if (path === '/' || /^[A-Za-z]:\/$/.test(path)) return path;
  return path.replace(/\/+$/, '');
}

/**
 * Shorten a viewer path to workspace-relative spelling when beneath `fileRoot`, without
 * URL-decoding, stripping line-number suffixes, or collapsing `..` segments on the client
 * (preserving literal `%`, `:1`, and symlink-sensitive paths).
 */
export function normalizeWorkspaceDisplayPath(path: string, fileRoot?: string): string {
  if (path === '') return '';
  const cleaned = stripTrailingSlashes(path);
  const shortened = fileRoot !== undefined && fileRoot !== ''
    ? reportFileNavigationPath(fileRoot, cleaned, '')
    : cleaned;
  const unprefixed = shortened.replace(/^\.\/(?=[^/])/, '');
  if (/^[A-Za-z][A-Za-z0-9+.-]*:/.test(unprefixed) || unprefixed === '~' || unprefixed.startsWith('~/')) {
    return shortened;
  }
  return unprefixed;
}

/** Split a file path into ancestor directory segments and leaf filename. */
export function splitWorkspaceRelativePath(path: string, fileRoot?: string): WorkspacePathParts {
  const normalizedPath = normalizeWorkspaceDisplayPath(path, fileRoot);
  if (normalizedPath === '') {
    return { normalizedPath: '', fileName: path, parentDir: '', directorySegments: [] };
  }
  const absolute = normalizedPath.startsWith('/');
  const literalPrefix = normalizedPath.startsWith('./');
  const trimmed = literalPrefix ? normalizedPath.slice(2) : normalizedPath;
  const parts = trimmed.split('/').filter((part) => part !== '');
  const fileName = parts.at(-1) ?? normalizedPath;
  const dirParts = parts.slice(0, -1);
  const directorySegments: BreadcrumbSegment[] = [];
  let acc = '';
  for (const name of dirParts) {
    if (acc === '') {
      acc = absolute ? `/${name}` : literalPrefix ? `./${name}` : name;
    } else {
      acc = `${acc}/${name}`;
    }
    directorySegments.push({ name, path: acc });
  }
  const parentDir = dirParts.length === 0
    ? (absolute ? '/' : '')
    : (directorySegments.at(-1)?.path ?? '');
  return {
    normalizedPath,
    fileName,
    parentDir,
    directorySegments,
  };
}

/**
 * Collapse only intermediate directory segments when a path is deeper than `maxVisibleDirs`.
 * Never invents an ellipsis when nothing is omitted.
 */
export function collapseBreadcrumbSegments(
  segments: readonly BreadcrumbSegment[],
  maxVisibleDirs = 3,
): CollapsedBreadcrumb {
  const limit = Math.max(2, maxVisibleDirs);
  if (segments.length <= limit) {
    return { leading: segments, collapsed: [], trailing: [] };
  }
  const trailingCount = limit - 1;
  return {
    leading: segments.slice(0, 1),
    collapsed: segments.slice(1, segments.length - trailingCount),
    trailing: segments.slice(segments.length - trailingCount),
  };
}

/**
 * Split a filename into a shrinkable leading `stem` and a fixed trailing `tail` so narrow
 * viewports middle-ellipsize long filenames while preserving the extension (e.g. `.md`).
 * Concatenating `stem + tail` always equals `fileName`.
 */
export function splitFileNameTail(fileName: string): FileNameTail {
  const fileChars = Array.from(fileName);
  const dot = fileName.lastIndexOf('.');
  const extension = dot > 0 && dot < fileName.length - 1 ? fileName.slice(dot) : '';
  const extChars = Array.from(extension);
  if (extChars.length > 0 && extChars.length <= 10 && fileChars.length > extChars.length) {
    const extraTail = fileChars.length > extChars.length + 4 ? 3 : 0;
    const tailLen = Math.min(
      extChars.length + extraTail,
      Math.max(extChars.length, Math.floor(fileChars.length / 2)),
    );
    const safeTail = Math.min(tailLen, fileChars.length - 1);
    return {
      stem: fileChars.slice(0, fileChars.length - safeTail).join(''),
      tail: fileChars.slice(fileChars.length - safeTail).join(''),
    };
  }
  return { stem: fileName, tail: '' };
}

/**
 * Format a directory path as a Quick Open directory-mode query (`''` at workspace root).
 * Ordinary workspace-relative directories receive a `./` prefix so Quick Open enters
 * literal directory listing mode rather than fzf search (avoiding `!`, `'`, `^`, `$`,
 * space syntax and fuzzy matches outside the folder).
 */
export function quickOpenQueryForDirectory(directory: string): string {
  const cleaned = stripTrailingSlashes(directory);
  if (cleaned === '') return '';
  const trailing = cleaned.endsWith('/') ? cleaned : `${cleaned}/`;
  if (isAbsoluteFilePath(trailing) || /^(?:~|\.\.?)\//.test(trailing)) {
    return trailing;
  }
  return `./${trailing}`;
}
