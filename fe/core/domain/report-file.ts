export type FileLineLocation = Readonly<{ line?: number; endLine?: number }>;
export type ReportFileLinkTarget = Readonly<{ path: string }> & FileLineLocation;

const uriScheme = /^[A-Za-z][A-Za-z0-9+.-]*:/;
const reservedScheme = /^(?:javascript|vbscript|data|blob|https?|file|mailto|tel|ftp|about|perga):/i;

/** Decoded filesystem paths keep their literal characters and segments. */
export function isFilePath(path: string): boolean {
  return path !== '' && !path.includes('\0');
}

export function parseFileLine(value: unknown): FileLineLocation {
  if (typeof value !== 'string' && typeof value !== 'number') return {};
  const match = /^(\d+)(?:-L?(\d+))?$/.exec(String(value));
  if (match === null) return {};
  const line = Number(match[1]);
  const endLine = match[2] === undefined ? undefined : Number(match[2]);
  return formatFileLine({ line, endLine }) === undefined ? {} : endLine === undefined ? { line } : { line, endLine };
}

export function formatFileLine({ line, endLine }: FileLineLocation): string | undefined {
  if (line === undefined || !Number.isSafeInteger(line) || line < 1) return undefined;
  if (endLine === undefined) return String(line);
  return Number.isSafeInteger(endLine) && endLine >= line ? `${line}-${endLine}` : undefined;
}

/** Split an editable query without interpreting escapes or losing its directory separator. */
export function splitFileOpenInput(input: string): ReportFileLinkTarget {
  const match = /(?:#L(\d+(?:-L?\d+)?)|:(\d+(?:-\d+)?))$/.exec(input);
  if (match === null || match.index === 0) return { path: input };
  const location = parseFileLine(match[1] ?? match[2]);
  return location.line === undefined ? { path: input } : { path: input.slice(0, match.index), ...location };
}

export function isFileOpenSpelling(input: string): boolean {
  return isFilePath(input) && !reservedScheme.test(input) && !input.startsWith('//') && !/^[A-Za-z][A-Za-z0-9+.-]*:\/\//.test(input);
}

export function parseFileOpenInput(input: string): ReportFileLinkTarget | null {
  const value = input.trim();
  if (!isFileOpenSpelling(value)) return null;
  const target = splitFileOpenInput(value);
  return target.path.endsWith('/') ? null : target;
}

/** Markdown decodes URL escapes once. Existing column and multi-range references keep opening their files. */
export function parseReportFileLink(destination: string): ReportFileLinkTarget | null {
  let decoded: string;
  try {
    decoded = decodeURIComponent(destination);
  } catch {
    return null;
  }
  if (decoded.includes('?') || destination.includes('\\') || decoded.startsWith('//')) return null;
  if (decoded.startsWith('file://')) {
    decoded = decoded.slice('file://'.length);
    if (!decoded.startsWith('/') || decoded.startsWith('//')) return null;
  } else if (reservedScheme.test(decoded)) return null;
  const hash = decoded.indexOf('#');
  const spelling = hash < 0 ? decoded : decoded.slice(0, hash);
  const hashLine = hash < 0 ? undefined : /^L(\d+(?:-L?\d+)?)$/.exec(decoded.slice(hash + 1))?.[1];
  // Preserve column references and the older discontiguous range spelling.
  const columns = /:(\d+):\d+$/.exec(spelling);
  const multiple = /:\d+(?:-\d+)?(?:,\d+(?:-\d+)?)+(?::\d+)?$/.exec(spelling);
  const suffix = multiple ?? columns;
  const target = suffix === null ? splitFileOpenInput(spelling) : {
    path: spelling.slice(0, suffix.index), ...parseFileLine(multiple === null ? columns?.[1] : undefined),
  };
  const unprefixed = target.path.replace(/^\.\/(?=[^/])/, '');
  const spellingIsSpecial = uriScheme.test(unprefixed) || unprefixed === '~' || unprefixed.startsWith('~/');
  const file = spellingIsSpecial ? target.path : unprefixed;
  if (file === '.' || uriScheme.test(file) || !isFilePath(file)) return null;
  return hashLine === undefined ? { ...target, path: file } : { path: file, ...parseFileLine(hashLine) };
}

export function isAbsoluteFilePath(path: string): boolean {
  return path.startsWith('/') || /^[A-Za-z]:\//.test(path);
}

export function fileParentDirectory(path: string): string | null {
  if (path === '' || path === '/' || /^[A-Za-z]:\/$/.test(path)) return null;
  const separator = path.lastIndexOf('/');
  if (separator === 0) return '/';
  if (separator === 2 && !path.startsWith('/') && isAbsoluteFilePath(path)) return path.slice(0, 3);
  return separator < 0 ? '' : path.slice(0, separator);
}

/** Join a document-relative link without resolving segments on the client. */
export function reportFileNavigationPath(workspaceRoot: string, path: string, basePath = ''): string {
  if (path === '~' || path.startsWith('~/')) return path;
  const fromDocument = isAbsoluteFilePath(path) || basePath === '' ? path : joinFilePath(basePath, path);
  if (!isAbsoluteFilePath(workspaceRoot)) return fromDocument;
  const prefix = workspaceRoot.replace(/\/+$/, '') + '/';
  if (!fromDocument.startsWith(prefix)) return fromDocument;
  const relative = fromDocument.slice(prefix.length).replace(/^\/+/, '');
  if (relative === '') return fromDocument;
  return relative === '~' || relative.startsWith('~/') ? './' + relative : relative;
}

export function joinFilePath(directory: string, name: string): string {
  return directory.endsWith('/') ? directory + name : directory + '/' + name;
}
