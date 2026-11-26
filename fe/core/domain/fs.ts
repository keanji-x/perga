// Filesystem reads the browser may make. `joinPath` is a parameter because `core/` may not
// import `web/src/ui/**`, where the path-joining rule lives; the app-layer adapter injects it.

import { z } from 'zod';
import type { WorkspaceFileSearchResultDto } from '../api/generated/wire.js';

import { diffSummaryDtoSchema, hostDirListDtoSchema, workspaceFileResultDtoSchema, type DiffSummaryDto } from '../api/perga-v1.js';
import type { ApiAbortSignal, ApiOperation } from '../api/types.js';
import { fileParentDirectory } from './report-file.js';
import type { FailureTable } from './failure-class.js';

const directoryEntryWireSchema = z.object({
  name: z.string(),
  is_dir: z.boolean(),
  is_symlink: z.boolean().optional(),
});

/** `parent` is `null` at the filesystem root and only there. */
export const directoryListingWireSchema = z.object({
  path: z.string(),
  parent: z.string().nullable(),
  entries: z.array(directoryEntryWireSchema),
});
export type DirectoryListingWire = z.infer<typeof directoryListingWireSchema>;

/** `path` omitted starts the walk at the server's `$HOME`; the query key is left off rather than sent empty. */
export function listDirectoryOperation(
  path?: string,
  options?: Readonly<{ targetId?: string }>,
): ApiOperation<DirectoryListingWire> {
  const base = '/api/v1/fs/listdir';
  const params: string[] = [];
  if (path !== undefined && path !== '') {
    params.push(`path=${encodeURIComponent(path)}`);
  }
  if (options?.targetId !== undefined && options.targetId !== '' && options.targetId !== 'local') {
    params.push(`targetId=${encodeURIComponent(options.targetId)}`);
  }
  const query = params.length === 0 ? '' : `?${params.join('&')}`;
  return {
    method: 'GET',
    path: `${base}${query}`,
    responseSchema: z.union([
      directoryListingWireSchema,
      hostDirListDtoSchema.transform((dto): DirectoryListingWire => ({
        path: dto.path,
        parent: dto.parent,
        entries: dto.entries.map((entry) => ({
          name: entry.name,
          is_dir: entry.isDir,
          is_symlink: entry.isSymlink,
        })),
      })),
    ]),
  };
}

/** `GET /api/fs/readfile`. Text only: the kernel answers 400 for a binary or non-UTF-8 file. */
const readFileWireSchema = z.object({
  path: z.string(),
  size: z.number(),
  text: z.string(),
  truncated: z.boolean(),
});
export type ReadFileWire = z.infer<typeof readFileWireSchema>;

/** A Track-scoped read via `GET /api/v1/tracks/{trackId}/files`: the kernel owns the workspace root. */
export function readTrackWorkspaceFileOperation(
  trackId: string,
  path: string,
): ApiOperation<ReadFileWire> {
  return {
    method: 'GET',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/files?path=${encodeURIComponent(path)}`,
    responseSchema: z.union([
      workspaceFileResultDtoSchema.refine(dto => !dto.isDirectory, 'Path is a directory.').transform((dto) => ({
        path: dto.relativePath || path,
        size: (dto.content ?? '').length,
        text: dto.content ?? '',
        truncated: dto.truncated,
      })),
      readFileWireSchema,
    ]),
  };
}

const workspaceFileSearchSchema = z.object({ paths: z.array(z.string()), truncated: z.boolean() }) satisfies z.ZodType<WorkspaceFileSearchResultDto>;

export function searchTrackWorkspaceFilesOperation(
  trackId: string, query: string, signal: ApiAbortSignal, limit = 50,
): ApiOperation<WorkspaceFileSearchResultDto> {
  return {
    method: 'GET',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/files/search?q=${encodeURIComponent(query)}&limit=${limit}`,
    responseSchema: workspaceFileSearchSchema,
    signal,
  };
}

export function listTrackWorkspaceDirectoryOperation(
  trackId: string,
  path?: string,
): ApiOperation<DirectoryListingWire> {
  const query = path === undefined || path === '' ? '' : `?path=${encodeURIComponent(path)}`;
  return {
    method: 'GET',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/files${query}`,
    responseSchema: z.union([
      workspaceFileResultDtoSchema.refine(dto => dto.isDirectory, 'Path is not a directory.').transform((dto) => ({
        path: dto.relativePath,
        parent: fileParentDirectory(dto.relativePath),
        entries: (dto.entries ?? []).map((entry) => ({
          name: entry.name,
          is_dir: entry.isDirectory,
        })),
      })),
      directoryListingWireSchema,
    ]),
  };
}

export function trackVcsDiffOperation(
  trackId: string,
  fromRev?: string,
  toRev?: string,
  filePath?: string,
): ApiOperation<DiffSummaryDto> {
  const parts: string[] = [];
  if (fromRev !== undefined && fromRev !== '') parts.push(`fromRev=${encodeURIComponent(fromRev)}`);
  if (toRev !== undefined && toRev !== '') parts.push(`toRev=${encodeURIComponent(toRev)}`);
  if (filePath !== undefined && filePath !== '') parts.push(`path=${encodeURIComponent(filePath)}`);
  const query = parts.join('&');
  return {
    method: 'GET',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/vcs-diff${query === '' ? '' : `?${query}`}`,
    responseSchema: diffSummaryDtoSchema,
  };
}

/** Raw image counterpart to {@link readTrackWorkspaceFileOperation}. */
export function trackWorkspaceRawFileUrl(
  trackId: string,
  path: string,
): string {
  return `/api/v1/tracks/${encodeURIComponent(trackId)}/files/raw?path=${encodeURIComponent(path)}`;
}

/** Filesystem reads bound to the Track execution environment. */
export type WorkspaceFilePort = Readonly<{
  readFile: (path: string) => Promise<ReadFileWire>;
  rawUrl: (path: string) => string;
}>;

/** `status` is the kernel's word: modified / added / deleted / untracked / renamed. */
export type GitChangedFileWire = Readonly<{ path: string; status: string; old_path?: string }>;
export type GitStatusWire = Readonly<{ repo_root: string; files: readonly GitChangedFileWire[] }>;
export type GitDiffWire = Readonly<{ path: string; status: string; head_text: string | null; working_text: string | null; truncated: boolean }>;

/**
 * The filesystem reads a card may make, as a port: `systems/**` holds no transport, so the reads
 * arrive as injected functions.
 */
export type CardFilesPort = Readonly<{
  listDirectory: (path: string) => Promise<DirectoryListingWire>;
  readFile: (path: string) => Promise<ReadFileWire>;
  gitStatus: (path: string) => Promise<GitStatusWire>;
  gitDiff: (path: string, oldPath?: string) => Promise<GitDiffWire>;
  /** The `<img src>` for an image file — see `rawFileUrl`. */
  rawUrl: (path: string) => string;
}>;

export type DirectoryListingEntry = Readonly<{
  name: string;
  path: string;
  isDirectory: boolean;
}>;

export type DirectoryListingView = Readonly<{
  path: string;
  parent: string | null;
  entries: readonly DirectoryListingEntry[];
}>;

/** Structurally the `DirectoryListing` `ui/directory-browser` renders. */
export function toDirectoryListing(
  wire: DirectoryListingWire,
  joinPath: (parent: string, name: string) => string,
): DirectoryListingView {
  return {
    path: wire.path,
    parent: wire.parent,
    entries: wire.entries.map((entry) => ({
      name: entry.name,
      path: joinPath(wire.path, entry.name),
      isDirectory: entry.is_dir,
    })),
  };
}

/**
 * What a failed filesystem read is, by the answers the fs routes give (`crates/calm-server/src/routes/fs.rs`:
 * `listdir`, `readfile`, `gitstatus`, `gitdiff`, and the Track workspace `readfile`). Only status and code decide:
 *
 * | answer                | when                                                                  | class     |
 * | --------------------- | --------------------------------------------------------------------- | --------- |
 * | 403 `forbidden`       | the server may not read the path (EACCES / EPERM)                     | `denied`  |
 * | 404 `path_not_found`  | the path, or a folder on the way to it, does not exist                | `missing` |
 * | 404 `not_found`       | Track workspace reads only: the Track itself is gone                  | `gone`    |
 * | 400 `bad_request`     | a directory or non-regular file, binary or non-UTF-8 text, a path     | `other`   |
 * |                       | outside any git repository              |           |
 * | anything else         | 5xx, 401, a lost or unreadable answer                                 | `other`   |
 *
 * `missing` and `gone` are told apart because their remedies differ: a path can be restored, a deleted Track cannot.
 */
export type FileReadClass = 'denied' | 'missing' | 'gone' | 'other';

export const FILE_READ_FAILURES: FailureTable<FileReadClass> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze([403]), code: 'forbidden', is: 'denied' as const }),
    Object.freeze({ status: Object.freeze([404]), code: 'path_not_found', is: 'missing' as const }),
    Object.freeze({ status: Object.freeze([404]), code: 'not_found', is: 'gone' as const }),
  ]),
  unauthorized: 'other',
  otherwise: 'other',
});
