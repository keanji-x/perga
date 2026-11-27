import { LayerDepthProvider, useLayerDismissal } from '@astryxdesign/core/Layer';
import { ReadOnlyCode } from '../../../ui/code/public.tsx';
import { usePortalLightbox } from '../../../ui/lightbox/public.tsx';
import { useEffect, useLayoutEffect, useMemo, useRef } from 'react';

import type { WorkspaceFilePort } from '../../../../../core/domain/fs.ts';
import type { TrackReport } from '../../../../../core/domain/report.ts';
import type { ReportSourceLinkTarget } from '../../../../../core/domain/report-source.ts';
import { fileParentDirectory, type ReportFileLinkTarget } from '../../../../../core/domain/report-file.ts';
import { FileReadError, useReportFileResource } from '../../../systems/fs-viewers/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import type { ReportLinkPreviewResources } from '../link-preview/public.tsx';
import { ReportDocument } from '../document/public.tsx';
import type { ReportSourceInsertion } from '../source/placement.ts';
import styles from './report-file-viewer.module.css';

export { FileLayoutNav, type FileLayoutNavProps } from './file-layout-nav.tsx';

function basename(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

/**
 * A file rendered as another document in the Report workspace. It deliberately
 * has no card head, directory tree or Diff mode: following evidence from prose
 * keeps the reader in the document vocabulary rather than moving them onto the
 * card board.
 */
export function ReportFileViewer({
  path, line, endLine, silent = false, openRequest, files, fileRoot, wide, onClose, hasBackFocus, onFileOpened, onOpenFileLink, onOpenSourceLink, linkPreview, sourceInsertion, restoreScroll = false, initialSourceMode, onSourceModeChange,
}: Readonly<{
  restoreScroll?: boolean;
  initialSourceMode?: boolean;
  onSourceModeChange?: (sourceMode: boolean) => void;
  path: string;
  line?: number;
  endLine?: number;
  silent?: boolean;
  openRequest?: number;
  files: WorkspaceFilePort;
  fileRoot: string;
  /** Code, text, images and pages may use the panel column when no card owns it. */
  wide: boolean;
  onClose: () => void;
  /** The owning page's file Back control can also dismiss with Escape. */
  hasBackFocus?: () => boolean;
  onFileOpened?: (path: string) => void;
  onOpenFileLink?: (target: ReportFileLinkTarget) => void;
  onOpenSourceLink?: (target: ReportSourceLinkTarget) => void;
  linkPreview?: ReportLinkPreviewResources;
  sourceInsertion?: ReportSourceInsertion;
}>) {
  const layerRef = useRef<HTMLDivElement | null>(null);
  const resource = useReportFileResource(path, files, { onOpened: onFileOpened, resolveImagePath: true });
  const highlightLines = useMemo(
    () => (line === undefined ? undefined : { start: line, end: endLine ?? line }),
    [line, endLine],
  );
  const ready = resource.kind === 'loaded' || resource.kind === 'html' || resource.kind === 'image';
  const isMarkdownFile = resource.kind === 'loaded' && resource.format === 'markdown';
  const markdownJumpKey = JSON.stringify([path, line, endLine, openRequest]);
  const [markdownSourceKey, setMarkdownSourceKey] = useState<string | null>(() => initialSourceMode === true ? markdownJumpKey : null);
  const showMarkdownSource = isMarkdownFile && highlightLines !== undefined && markdownSourceKey === markdownJumpKey;
  const markdown = isMarkdownFile && !showMarkdownSource;
  const markdownText = markdown ? resource.text : null;
  const report = useMemo<TrackReport | null>(() => markdownText === null ? null
    : { summary: '', body: markdownText, blocks: null }, [markdownText]);
  const imageUrl = resource.kind === 'image' ? resource.url : null;
  const imageAlt = resource.kind === 'image' ? resource.path : '';
  const imageMedia = useMemo(() => (imageUrl === null ? [] : [{ src: imageUrl, alt: imageAlt }]), [imageAlt, imageUrl]);
  const imageLightbox = usePortalLightbox(imageMedia);

  useEffect(() => {
    if (!silent) layerRef.current?.focus({ preventScroll: true });
  }, [path, line, endLine, silent, openRequest]);

  useLayoutEffect(() => {
    const container = layerRef.current;
    if (!ready || container === null) return;
    if (restoreScroll || !markdown || highlightLines === undefined) return;
    const target = container.querySelector<HTMLElement>('[data-nc-markdown-highlight]');
    if (target === null) return;
    const containerRect = container.getBoundingClientRect();
    const targetRect = target.getBoundingClientRect();
    const offset = targetRect.height < container.clientHeight
      ? (container.clientHeight - targetRect.height) / 2
      : 24;
    const nextTop = container.scrollTop + (targetRect.top - containerRect.top) - offset;
    container.scrollTop = Math.max(0, nextTop);
  }, [ready, markdown, highlightLines, openRequest, markdownText, restoreScroll]);

  useLayerDismissal({
    isActive: true,
    onDismiss: () => onClose(),
    getContainer: () => layerRef.current,
    isPresent: () => document.activeElement === document.body
      || layerRef.current?.contains(document.activeElement) === true
      || hasBackFocus?.() === true,
  });

  const markdownViewToggle = isMarkdownFile && highlightLines !== undefined ? (
    <div
      className={markdown ? `${styles.views} ${styles.markdownViews}` : styles.views}
      role="group"
      aria-label="Markdown view"
    >
      <button
        type="button"
        className={styles.view}
        aria-pressed={markdown}
        onClick={() => { setMarkdownSourceKey(null); onSourceModeChange?.(false); }}
      >
        Preview
      </button>
      <button
        type="button"
        className={styles.view}
        aria-pressed={showMarkdownSource}
        onClick={() => { setMarkdownSourceKey(markdownJumpKey); onSourceModeChange?.(true); }}
      >
        Source
      </button>
    </div>
  ) : null;

  return (
    <LayerDepthProvider><div
      ref={layerRef}
      className={highlightLines === undefined || markdown ? styles.layer : `${styles.layer} ${styles.lineJump}`}
      data-nc-report-file-viewer=""
      data-nc-report-file-ready={ready ? '' : undefined}
      data-nc-report-file-wide={wide ? '' : undefined}
      data-nc-escape-layer=""
      role="region"
      aria-label={`File ${path}`}
      tabIndex={-1}
    >
      <div className={`${styles.body} ${markdown ? styles.bodyProse : wide ? styles.bodyWide : ''}`}>
        {resource.kind === 'loading' && <p className={styles.state} role="status">Loading file…</p>}
        {resource.kind === 'error' && <div className={styles.error}>
          <FileReadError failure={resource.failure} resource={resource.resource} onRetry={resource.retry} />
        </div>}
        {resource.kind === 'image' && (
          <figure className={styles.imageWrap}>
            <button
              type="button"
              className={styles.imageButton}
              aria-haspopup="dialog"
              aria-label={`Inspect ${basename(resource.path)}`}
              onClick={() => imageLightbox.open()}
            >
              <img
                className={styles.image}
                src={resource.url}
                alt={resource.path}
                onLoad={resource.onLoad}
                onError={() => {
                  imageLightbox.close();
                  resource.onError();
                }}
              />
            </button>
            {imageLightbox.portal}
          </figure>
        )}
        {resource.kind === 'html' && <HtmlFileView
          key={JSON.stringify([path, line, endLine, openRequest])}
          path={resource.path}
          text={resource.text}
          url={resource.url}
          highlightLines={highlightLines}
          restoreScroll={restoreScroll}
          initialSourceMode={initialSourceMode}
          onSourceModeChange={onSourceModeChange}
        />}
        {resource.kind === 'loaded' && markdown && (
          <div className={styles.markdown}>
            {markdownViewToggle}
            {resource.truncated && <p className={styles.notice}>Showing the first 2 MiB of this file.</p>}
            <ReportDocument
              report={report}
              empty={<></>}
              sourceInsertion={sourceInsertion}
              fileRoot={fileRoot}
              fileBasePath={fileParentDirectory(resource.path) ?? ''}
              highlightLines={highlightLines}
              onOpenFileLink={onOpenFileLink}
              onOpenSourceLink={onOpenSourceLink}
              linkPreview={linkPreview}
            />
          </div>
        )}
        {resource.kind === 'loaded' && !markdown && (
          <div className={styles.sourceWrap}>
            {markdownViewToggle}
            {resource.truncated && <p className={styles.notice}>Showing the first 2 MiB of this file.</p>}
            <div data-nc-report-file-source=""><ReadOnlyCode text={resource.text} source={{ kind: 'filename', value: resource.path }}
              scrollHighlight={!restoreScroll}
              highlightLines={highlightLines} highlightRequest={openRequest} wrapLines={showMarkdownSource} /></div>
          </div>
        )}
      </div>
    </div></LayerDepthProvider>
  );
}

function HtmlFileView({ path, text, url, highlightLines, restoreScroll, initialSourceMode, onSourceModeChange }: Readonly<{
  path: string;
  text: string;
  url: string;
  restoreScroll?: boolean;
  initialSourceMode?: boolean;
  onSourceModeChange?: (sourceMode: boolean) => void;
  highlightLines?: Readonly<{ start: number; end: number }>;
}>) {
  const [sourceMode, setSourceMode] = useState(initialSourceMode ?? highlightLines !== undefined);
  return <div className={styles.pageWrap}>
    <div className={styles.views} role="group" aria-label="Page view">
      <button type="button" className={styles.view} aria-pressed={!sourceMode} onClick={() => { setSourceMode(false); onSourceModeChange?.(false); }}>Page</button>
      <button type="button" className={styles.view} aria-pressed={sourceMode} onClick={() => { setSourceMode(true); onSourceModeChange?.(true); }}>Source</button>
    </div>
    {!sourceMode
      ? <WorkspaceHtmlPage url={url} title={path} />
      : <div className={styles.pageSource} data-nc-report-file-source=""><ReadOnlyCode
        text={text} source={{ kind: 'filename', value: path }} highlightLines={highlightLines}
        scrollHighlight={!restoreScroll} /></div>}
  </div>;
}

// The raw file route applies the same sandbox when a page is opened directly.
function WorkspaceHtmlPage({ url, title }: Readonly<{ url: string; title: string }>) {
  return <iframe className={styles.htmlPage} src={url} title={title} sandbox="allow-scripts"
    referrerPolicy="no-referrer" loading="lazy" />;
}
