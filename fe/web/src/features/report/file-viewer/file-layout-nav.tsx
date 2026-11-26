import { Fragment, useMemo } from 'react';

import {
  collapseBreadcrumbSegments,
  quickOpenQueryForDirectory,
  splitFileNameTail,
  splitWorkspaceRelativePath,
  type BreadcrumbSegment,
} from '../../../../../core/domain/report-file-layout.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import { useCompactViewport } from '../../../ui/viewport/public.ts';
import styles from './file-layout-nav.module.css';

export type FileLayoutNavProps = Readonly<{
  path: string;
  /** Full path shown in the native tooltip (`title`), defaulting to `path`. */
  fullPath?: string;
  /** Optional line range suffix (e.g. `:125-127`) appended quietly after the filename. */
  lineSuffix?: string;
  fileRoot: string;
  onOpenQuickOpen?: (initialQuery: string) => void;
}>;

/**
 * Stateless header breadcrumb trigger hosted in `TrackPage`'s file-viewer title slot.
 * - Desktop (`>= 60rem`): clickable path segments opening Quick Open scoped to that folder,
 *   or clicking the filename to open global Quick Open (`Recent` + `Files`).
 * - Mobile (`< 60rem`): single compact tap target showing the filename with extension-preserving
 *   middle ellipsis and opening global Quick Open (`Recent` + `Files`).
 */
export function FileLayoutNav({ path, fullPath, lineSuffix, fileRoot, onOpenQuickOpen }: FileLayoutNavProps) {
  const compactViewport = useCompactViewport();
  const pathParts = useMemo(() => splitWorkspaceRelativePath(path, fileRoot), [path, fileRoot]);
  const tooltipPath = fullPath ?? path;

  if (onOpenQuickOpen === undefined) {
    return (
      <span className={styles.staticPath} title={tooltipPath}>
        {pathParts.normalizedPath}{lineSuffix ?? ''}
      </span>
    );
  }

  if (compactViewport) {
    const mobileFile = splitFileNameTail(pathParts.fileName);
    return (
      <button
        type="button"
        className={styles.mobileTrigger}
        title={tooltipPath}
        aria-label={`File location: ${pathParts.normalizedPath}${lineSuffix ?? ''}`}
        aria-haspopup="dialog"
        onClick={() => { onOpenQuickOpen(''); }}
      >
        <span className={styles.mobileTriggerText}>
          <span className={styles.mobileFileName}>
            <span className={styles.mobileFileStem}>{mobileFile.stem}</span>
            {mobileFile.tail !== '' && <span className={styles.mobileFileTail}>{mobileFile.tail}</span>}
          </span>
          {lineSuffix !== undefined && <span className={styles.lineSuffix}>{lineSuffix}</span>}
        </span>
        <span className={styles.chevron} aria-hidden="true"><Icon name="chevron-down" size="sm" /></span>
      </button>
    );
  }

  const collapsed = collapseBreadcrumbSegments(pathParts.directorySegments, 3);
  const leadingPrefix = pathParts.normalizedPath.startsWith('/') ? '/' : pathParts.normalizedPath.startsWith('./') ? './' : '';
  const deepestCollapsed = collapsed.collapsed.at(-1);

  const renderSegment = (segment: BreadcrumbSegment) => (
    <Fragment key={segment.path}>
      <button
        type="button"
        className={styles.segmentButton}
        aria-label={`Folder ${segment.name}`}
        aria-haspopup="dialog"
        onClick={() => { onOpenQuickOpen(quickOpenQueryForDirectory(segment.path)); }}
      >
        <span className={styles.segmentText}>{segment.name}</span>
      </button>
      <span className={styles.separator} aria-hidden="true">/</span>
    </Fragment>
  );

  return (
    <nav className={styles.breadcrumb} aria-label="File path" title={tooltipPath}>
      {leadingPrefix !== '' && <span className={styles.separator} aria-hidden="true">{leadingPrefix}</span>}
      {collapsed.leading.map(renderSegment)}
      {deepestCollapsed !== undefined && (
        <>
          <button
            type="button"
            className={styles.segmentButton}
            aria-label="Intermediate folders"
            aria-haspopup="dialog"
            onClick={() => { onOpenQuickOpen(quickOpenQueryForDirectory(deepestCollapsed.path)); }}
          >
            <span className={styles.segmentText}>…</span>
          </button>
          <span className={styles.separator} aria-hidden="true">/</span>
        </>
      )}
      {collapsed.trailing.map(renderSegment)}
      <button
        type="button"
        className={styles.fileButton}
        aria-label={`Open file switcher for ${pathParts.fileName}${lineSuffix ?? ''}`}
        aria-haspopup="dialog"
        onClick={() => { onOpenQuickOpen(''); }}
      >
        <span className={styles.segmentText}>{pathParts.fileName}</span>
        {lineSuffix !== undefined && <span className={styles.lineSuffix}>{lineSuffix}</span>}
        <span className={styles.chevron} aria-hidden="true"><Icon name="chevron-down" size="sm" /></span>
      </button>
    </nav>
  );
}
