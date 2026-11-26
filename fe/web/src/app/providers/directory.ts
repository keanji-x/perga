// The `ListDirectory` port the directory browser takes as a prop, bound to the real transport; the
// composition layer is the one place that may hold both the operation and the browser's path-joining rule.

import { workspaceFileResultDtoSchema } from '../../../../core/api/perga-v1.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ApiError } from '../../../../core/domain/failure-class.ts';
import {
  listDirectoryOperation, listTrackWorkspaceDirectoryOperation, readTrackWorkspaceFileOperation,
  toDirectoryListing, trackVcsDiffOperation, trackWorkspaceRawFileUrl,
  type CardFilesPort, type DirectoryListingWire, type GitDiffWire, type GitStatusWire, type ReadFileWire, type WorkspaceFilePort,
} from '../../../../core/domain/fs.ts';
import { isAbsoluteFilePath, reportFileNavigationPath } from '../../../../core/domain/report-file.ts';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import { DIRECTORY_LIST_FAILED,joinDirectoryPath,type ListDirectory } from '../../ui/directory-browser/public.tsx';
import { runOperation } from './query-operation.ts';

/**
 * `joinDirectoryPath` is passed in: the directory browser owns how a listing's rows are addressed, and the decoder must
 * agree. A failed listing rejects with the sentence the shared read rule gives it, which is what the browser shows.
 */
export function createDirectoryLister(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  targetId?: string,
): ListDirectory {
  return async (path) => toDirectoryListing(
    await runOperation(transport, listDirectoryOperation(path, { targetId }), unauthorized).catch((error: unknown) => {
      throw new Error(readErrorText(error, DIRECTORY_LIST_FAILED), { cause: error });
    }),
    joinDirectoryPath,
  );
}

/** Bind reads to the Track execution environment. */
export function createTrackWorkspaceFilesPort(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  trackId: string,
): WorkspaceFilePort {
  return Object.freeze({
    readFile: (path) => runOperation(
      transport,
      readTrackWorkspaceFileOperation(trackId, path),
      unauthorized,
    ),
    rawUrl: (path) => trackWorkspaceRawFileUrl(trackId, path),
  });
}

function normalizeWorkspaceRoot(workspaceRoot: string): string {
  if (workspaceRoot === '' || workspaceRoot === '.') return '.';
  if (/^[A-Za-z]:\/$/.test(workspaceRoot)) return workspaceRoot;
  const trimmed = workspaceRoot.replace(/\/+$/, '');
  return trimmed === '' ? '/' : trimmed;
}

function toTrackFilePath(root: string, targetPath: string): string {
  const cleaned = targetPath === '/' || /^[A-Za-z]:\/$/.test(targetPath) ? targetPath : targetPath.replace(/\/+$/, '');
  if (cleaned === '' || cleaned === '.' || cleaned === root) return '';
  return reportFileNavigationPath(root, cleaned);
}

/** Track-scoped `CardFilesPort` backed by `/api/v1/tracks/{trackId}/files`, `/files/raw`, and `/vcs-diff`. */
export function createTrackCardFilesPort(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  trackId: string,
  workspaceRoot: string,
): CardFilesPort {
  const root = normalizeWorkspaceRoot(workspaceRoot);
  return Object.freeze({
    listDirectory: async (targetPath): Promise<DirectoryListingWire> => {
      const rel = toTrackFilePath(root, targetPath);
      const wire = await runOperation(
        transport,
        listTrackWorkspaceDirectoryOperation(trackId, rel),
        unauthorized,
      );
      const absolute = (path: string) => isAbsoluteFilePath(path) ? path
        : path === '' || path === '.' ? root : joinDirectoryPath(root, path);
      return { ...wire, path: absolute(wire.path), parent: wire.parent === null ? null : absolute(wire.parent) };
    },
    readFile: async (targetPath): Promise<ReadFileWire> => {
      const rel = toTrackFilePath(root, targetPath);
      const { method, path } = readTrackWorkspaceFileOperation(trackId, rel);
      const dto = await runOperation(
        transport,
        { method, path, responseSchema: workspaceFileResultDtoSchema },
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
        }))
        : diff.changedFiles.map((filePath) => ({ path: filePath, status: 'modified' }));
      return {
        repo_root: root,
        files,
      };
    },
    gitDiff: async (targetPath): Promise<GitDiffWire> => {
      const rel = toTrackFilePath(root, targetPath).replace(/^\.?\//, '');
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
    rawUrl: (targetPath) => trackWorkspaceRawFileUrl(trackId, toTrackFilePath(root, targetPath)),
  });
}
