// The `ListDirectory` port the directory browser takes as a prop, bound to the real transport; the
// composition layer is the one place that may hold both the operation and the browser's path-joining rule.

import {
  gitDiffOperation, gitStatusOperation, listDirectoryOperation, listTrackWorkspaceDirectoryOperation,
  rawFileUrl, readFileOperation, readTrackWorkspaceFileOperation, toDirectoryListing,
  trackVcsDiffOperation, trackWorkspaceRawFileUrl,
  type CardFilesPort, type DirectoryListingWire, type GitDiffWire, type GitStatusWire, type ReadFileWire, type WorkspaceFilePort,
} from '../../../../core/domain/fs.ts';
import { workspaceFileResultDtoSchema } from '../../../../core/api/schemas.ts';
import { ApiError } from '../../../../core/domain/failure-class.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { DIRECTORY_LIST_FAILED, joinDirectoryPath, type ListDirectory } from '../../ui/directory-browser/public.tsx';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import { runOperation } from './queries.ts';

/**
 * `joinDirectoryPath` is passed in: the directory browser owns how a listing's rows are addressed, and the decoder must
 * agree. A failed listing rejects with the sentence the shared read rule gives it, which is what the browser shows.
 */
export function createDirectoryLister(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  pergaMode = false,
  targetId?: string,
): ListDirectory {
  return async (path) => toDirectoryListing(
    await runOperation(transport, listDirectoryOperation(path, { perga: pergaMode, targetId }), unauthorized).catch((error: unknown) => {
      throw new Error(readErrorText(error, DIRECTORY_LIST_FAILED), { cause: error });
    }),
    joinDirectoryPath,
  );
}

/** The same reads, as the port a card is handed: plain functions, no query cache in front of a filesystem that is already the source of truth. */
export function createCardFilesPort(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  shareLinkId?: string,
): CardFilesPort {
  return Object.freeze({
    listDirectory: (path) => runOperation(transport, listDirectoryOperation(path), unauthorized),
    readFile: (path) => runOperation(transport, readFileOperation(path), unauthorized),
    gitStatus: (path) => runOperation(transport, gitStatusOperation(path), unauthorized),
    gitDiff: (path, oldPath) => runOperation(transport, gitDiffOperation(path, oldPath), unauthorized),
    rawUrl: path => withShareLink(rawFileUrl(path), shareLinkId),
  });
}

/** Reads for agent-authored Report links; never accepts an absolute root from the browser — the kernel applies containment. */
export function createTrackWorkspaceFilesPort(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  trackId: string,
  shareLinkId?: string,
): WorkspaceFilePort {
  return Object.freeze({
    readFile: (path) => runOperation(
      transport,
      readTrackWorkspaceFileOperation(trackId, path),
      unauthorized,
    ),
    rawUrl: (path) => withShareLink(trackWorkspaceRawFileUrl(trackId, path), shareLinkId),
  });
}

function normalizeWorkspaceRoot(workspaceRoot: string): string {
  if (workspaceRoot === '' || workspaceRoot === '.') return '.';
  const trimmed = workspaceRoot.replace(/\/+$/, '');
  return trimmed === '' ? '/' : trimmed;
}

function toWorkspaceRelative(workspaceRoot: string, targetPath: string): string {
  const root = normalizeWorkspaceRoot(workspaceRoot);
  const cleaned = targetPath.replace(/\/+$/, '');
  if (cleaned === '' || cleaned === '.' || cleaned === root) return '';
  if (root === '/' && cleaned.startsWith('/')) return cleaned.slice(1);
  if (cleaned.startsWith(`${root}/`)) return cleaned.slice(root.length + 1);
  return cleaned.replace(/^\.?\//, '');
}

/** Track-scoped `CardFilesPort` backed by `/api/v1/tracks/{trackId}/files`, `/files/raw`, and `/vcs-diff`. */
export function createTrackCardFilesPort(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  trackId: string,
  workspaceRoot: string,
  shareLinkId?: string,
): CardFilesPort {
  const root = normalizeWorkspaceRoot(workspaceRoot);
  return Object.freeze({
    listDirectory: async (targetPath): Promise<DirectoryListingWire> => {
      const rel = toWorkspaceRelative(root, targetPath);
      const wire = await runOperation(
        transport,
        listTrackWorkspaceDirectoryOperation(trackId, rel),
        unauthorized,
      );
      const resolvedRel = wire.path.replace(/^\/+/, '');
      const fullPath = resolvedRel === '' ? root : joinDirectoryPath(root, resolvedRel);
      const parentPath = resolvedRel === ''
        ? null
        : resolvedRel.includes('/')
          ? joinDirectoryPath(root, resolvedRel.slice(0, resolvedRel.lastIndexOf('/')))
          : root;
      return {
        path: fullPath,
        parent: parentPath,
        entries: wire.entries,
      };
    },
    readFile: async (targetPath): Promise<ReadFileWire> => {
      const rel = toWorkspaceRelative(root, targetPath);
      const query = rel === '' ? '' : `?path=${encodeURIComponent(rel)}`;
      const dto = await runOperation(
        transport,
        {
          method: 'GET',
          path: `/api/v1/tracks/${encodeURIComponent(trackId)}/files${query}`,
          responseSchema: workspaceFileResultDtoSchema,
        },
        unauthorized,
      );
      if (dto.isDirectory) {
        throw new ApiError({ kind: 'http', status: 400, code: 'bad_request', message: 'Path is a directory.' });
      }
      if (dto.isBinary) {
        return {
          path: targetPath,
          size: 0,
          text: '[Binary file]',
          truncated: false,
        };
      }
      const content = dto.content ?? '';
      return {
        path: targetPath,
        size: content.length,
        text: content,
        truncated: dto.truncated,
      };
    },
    gitStatus: async (): Promise<GitStatusWire> => {
      const diff = await runOperation(
        transport,
        trackVcsDiffOperation(trackId),
        unauthorized,
      );
      const files = diff.files !== undefined
        ? diff.files.map((entry) => ({
          path: entry.path,
          status: entry.status,
          ...(entry.oldPath ? { old_path: entry.oldPath } : {}),
        }))
        : diff.changedFiles.map((filePath) => ({ path: filePath, status: 'modified' }));
      return {
        repo_root: root,
        files,
      };
    },
    gitDiff: async (targetPath): Promise<GitDiffWire> => {
      const rel = toWorkspaceRelative(root, targetPath);
      const diff = await runOperation(
        transport,
        trackVcsDiffOperation(trackId, undefined, undefined, rel),
        unauthorized,
      );
      const matched = diff.files?.find((entry) => entry.path === rel) ?? diff.files?.[0];
      const status = matched?.status ?? 'modified';
      return {
        path: rel,
        status,
        head_text: matched?.headText !== undefined && matched.headText !== null
          ? matched.headText
          : status === 'added' || status === 'untracked'
            ? null
            : '',
        working_text: matched?.workingText !== undefined && matched.workingText !== null
          ? matched.workingText
          : status === 'deleted'
            ? null
            : matched?.patch ?? diff.diffText,
        truncated: diff.truncated,
      };
    },
    rawUrl: (targetPath) => withShareLink(
      trackWorkspaceRawFileUrl(trackId, toWorkspaceRelative(root, targetPath), { raw: true }),
      shareLinkId,
    ),
  });
}

function withShareLink(url: string, id?: string): string {
  return id === undefined ? url : `${url}${url.includes('?') ? '&' : '?'}share_link=${encodeURIComponent(id)}`;
}
