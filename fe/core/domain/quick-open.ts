import { fileParentDirectory, isAbsoluteFilePath, isFileOpenSpelling, joinFilePath, splitFileOpenInput, type ReportFileLinkTarget } from './report-file.js';
import type { DirectoryListingWire } from './fs.js';

export type QuickOpenQuery =
  | Readonly<{ kind: 'search'; query: string; target: ReportFileLinkTarget }>
  | Readonly<{ kind: 'directory'; directory: string; prefix: string; target: ReportFileLinkTarget }>;

export function quickOpenQuery(input: string): QuickOpenQuery {
  const target = splitFileOpenInput(input.trimStart());
  if ((isAbsoluteFilePath(target.path) || /^(?:~|\.\.?)\/|\/\.\.\//.test(target.path)) && isFileOpenSpelling(target.path)) {
    const separator = target.path.lastIndexOf('/');
    const directory = fileParentDirectory(target.path) ?? target.path;
    return { kind: 'directory', directory, prefix: target.path.slice(separator + 1), target };
  }
  return { kind: 'search', query: target.path.trim(), target };
}

export type QuickOpenEntry = Readonly<{ path: string; isDirectory?: boolean }>;

export function quickOpenDirectoryEntries(directory: string, prefix: string, entries: DirectoryListingWire['entries']): QuickOpenEntry[] {
  const lower = prefix.toLowerCase();
  return entries.filter(entry => entry.name.toLowerCase().startsWith(lower))
    .slice(0, 50)
    .map(entry => ({ path: joinFilePath(directory, entry.name), isDirectory: entry.is_dir }));
}
