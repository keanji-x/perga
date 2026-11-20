// The source panel: what a `neige://source/…` citation opens.
// Captured evidence has an inert reading view and an exact original-text view.

import { useEffect, useRef, type ReactNode, type RefObject } from 'react';
import { IconButton } from '@astryxdesign/core/IconButton';
import { LayerDepthProvider, useLayerDismissal } from '@astryxdesign/core/Layer';

import type {
  ReportSourceLinkTarget, SourceResolution, TrackSourceDetail,
} from '../../../../../core/domain/report-source.ts';
import { sourceHighlight } from '../../../../../core/domain/report-source.ts';
import { readFailureText } from '../../../../../core/domain/read-failure.ts';
import { useState } from '../../../ui/state/public.ts';
import { ProseBlock } from '../document/content.tsx';
import { ErrorBox } from '../../../ui/error-box/public.tsx';
import { CardFrame } from '../../../ui/card-frame/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import { SOURCE_PANEL_COPY, SOURCE_PROVENANCE_COPY } from './copy.ts';
import styles from './source.module.css';

export { SOURCE_PANEL_COPY, SOURCE_PROVENANCE_COPY } from './copy.ts';

export { ReportSourceCitation } from './citation.tsx';

export type ReportSourcePanelProps = Readonly<{
  /** The citation that opened the panel. */
  target: ReportSourceLinkTarget;
  /** The app's read of `target.sourceId`; ignored when the link never named one. */
  resolution: SourceResolution;
  /** Repeats the read after a transport failure — a read, never a write. */
  onRetry: () => void;
  /** Hover previews must not scroll the underlying report. */
  scrollToQuote?: boolean;
  /** Hover readers may start formatted even when the citation carries a quote. */
  initialView?: 'reading' | 'original';
}>;

/** The source's accessible name uses its title once loaded. */
export function reportSourcePanelTitle(resolution: SourceResolution): string {
  return resolution.status === 'ok' ? resolution.source.title : SOURCE_PANEL_COPY.panelTitle;
}

/** An explicitly opened citation in the document's reading column. */
export function ReportSourceReader({ selection, onClose, ...panel }: Omit<ReportSourcePanelProps, 'target'> & Readonly<{
  selection: Readonly<{ target: ReportSourceLinkTarget; opener: HTMLElement | null }>;
  onClose: () => void;
}>) {
  const reader = useRef<HTMLDivElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    const active = document.activeElement;
    if (active !== selection.opener && active !== document.body) return;
    const source = reader.current;
    source?.focus({ preventScroll: true });
    if (document.activeElement === source) closeButton.current?.scrollIntoView({ block: 'nearest' });
  }, [selection]);
  const close = () => {
    const source = reader.current;
    if (source?.contains(document.activeElement)) {
      const { opener } = selection;
      if (opener?.isConnected) opener.focus();
      if (document.activeElement !== opener) source.closest<HTMLElement>('[data-nc-report]')?.focus({ preventScroll: true });
    }
    onClose();
  };
  useLayerDismissal({
    isActive: true,
    getContainer: () => reader.current,
    isPresent: () => reader.current?.contains(document.activeElement) ?? false,
    onDismiss: close,
  });
  return <LayerDepthProvider><CardFrame ref={reader} role="region" aria-label={reportSourcePanelTitle(panel.resolution)}
    tabIndex={-1} elevation="flat" className={styles.reader}>
    <ReportSourcePanel {...panel} target={selection.target} closeAction={
      <IconButton ref={closeButton} className={styles.readerClose} label={SOURCE_PANEL_COPY.closeLabel} variant="ghost" size="sm"
        icon={<Icon name="close" size="sm" />} onClick={close} />
    } />
  </CardFrame></LayerDepthProvider>;
}

export function ReportSourcePanel({ target, resolution, onRetry, scrollToQuote = true, initialView, closeAction }: ReportSourcePanelProps & Readonly<{
  closeAction?: ReactNode;
}>) {
  const scrollRoot = useRef<HTMLDivElement>(null);
  const inline = closeAction !== undefined;
  const provenance = target.sourceId !== null && resolution.status === 'ok' ? resolution.source.provenance : null;
  const body = target.sourceId === null
    ? <Missing target={target} reason="malformed" />
    : resolution.status === 'missing'
      ? <Missing target={target} reason="dangling" />
      : resolution.status === 'loading'
        ? <p className={styles.state} role="status">{SOURCE_PANEL_COPY.loading}</p>
        : resolution.status === 'error'
          ? <ErrorBox message={readFailureText(resolution.failure, '无法读取来源。')} onRetry={onRetry} />
          : <Source key={`${resolution.source.source_id}:${target.quoteId ?? ''}:${initialView ?? ''}`} source={resolution.source} target={target}
            scrollToQuote={scrollToQuote} scrollRoot={inline ? scrollRoot : undefined}
            initialView={initialView ?? (target.quoteId === null ? 'reading' : 'original')} />;
  return (
    <div className={styles.panel} data-nc-report-source="">
      {(provenance !== null || inline) && <div className={styles.panelBar}>
        {provenance !== null && <span className={styles.badge} data-nc-report-source-provenance={provenance}>
          {SOURCE_PROVENANCE_COPY[provenance]}
        </span>}
        {closeAction}
      </div>}
      {inline ? <div ref={scrollRoot} className={styles.readerScroll}>{body}</div> : body}
    </div>
  );
}

/** The missing state, in one shape for its three causes: dangling id, malformed link, or an anchor the row does not carry. */
function MissingNotice({ destination, reason, title, detail }: {
  destination: string;
  reason: 'dangling' | 'malformed' | 'anchor';
  title: string;
  detail: string;
}) {
  const Heading = reason === 'anchor' ? 'h3' : 'h2';
  return (
    <section className={styles.missing} data-nc-report-source-missing={reason} aria-live="polite">
      <Heading className={styles.missingTitle}>{title}</Heading>
      <p className={styles.state}>{detail}</p>
      <p className={styles.destination}>
        <span className={styles.metaLabel}>{SOURCE_PANEL_COPY.destinationLabel}</span>
        <code className={styles.destinationCode}>{destination}</code>
      </p>
    </section>
  );
}

function Missing({ target, reason }: { target: ReportSourceLinkTarget; reason: 'dangling' | 'malformed' }) {
  return (
    <MissingNotice
      destination={target.destination}
      reason={reason}
      title={SOURCE_PANEL_COPY.missingTitle}
      detail={reason === 'dangling' ? SOURCE_PANEL_COPY.missingDangling : SOURCE_PANEL_COPY.missingMalformed}
    />
  );
}

function Source({ source, target, scrollToQuote, scrollRoot, initialView }: {
  source: TrackSourceDetail; target: ReportSourceLinkTarget; scrollToQuote: boolean;
  scrollRoot?: RefObject<HTMLDivElement | null>; initialView: 'reading' | 'original';
}) {
  const { quoteId } = target;
  const [original, setOriginal] = useState(initialView === 'original');
  const highlight = quoteId === null ? null : sourceHighlight(source, quoteId);
  const anchorMissed = quoteId !== null && highlight === null;
  const markRef = useRef<HTMLElement | null>(null);

  const placed = highlight !== null;
  useEffect(() => {
    if (!placed || !original || !scrollToQuote) return;
    const mark = markRef.current;
    if (mark === null) return;
    const root = scrollRoot?.current;
    if (root == null) {
      mark.scrollIntoView({ block: 'center' });
      return;
    }
    const { top, height } = mark.getBoundingClientRect();
    root.scrollTop += top - root.getBoundingClientRect().top - root.clientTop + (height - root.clientHeight) / 2;
  }, [placed, original, quoteId, source.source_id, scrollToQuote, scrollRoot]);

  return (
    <article className={styles.source}>
      <header className={styles.head}>
        <h2 className={styles.title}>{source.title}</h2>
        <dl className={styles.meta}>
          {source.published_at !== undefined && source.published_at !== '' && (
            <div className={styles.metaRow}>
              <dt className={styles.metaLabel}>{SOURCE_PANEL_COPY.publishedAt}</dt>
              <dd className={styles.metaValue}>{source.published_at}</dd>
            </div>
          )}
          <div className={styles.metaRow}>
            <dt className={styles.metaLabel}>{SOURCE_PANEL_COPY.capturedAt}</dt>
            <dd className={styles.metaValue}><time dateTime={source.captured_at}>{capturedAtLabel(source.captured_at)}</time></dd>
          </div>
          <Origin source={source} />
        </dl>
      </header>
      {anchorMissed && (
        <MissingNotice
          destination={target.destination}
          reason="anchor"
          title={SOURCE_PANEL_COPY.anchorMissingTitle}
          detail={SOURCE_PANEL_COPY.anchorMissingDetail}
        />
      )}
      <div className={styles.views} role="group" aria-label={SOURCE_PANEL_COPY.viewLabel}>
        <button type="button" className={styles.view} aria-pressed={!original} onClick={() => { setOriginal(false); }}>{SOURCE_PANEL_COPY.reading}</button>
        <button type="button" className={styles.view} aria-pressed={original} onClick={() => { setOriginal(true); }}>{SOURCE_PANEL_COPY.original}</button>
      </div>
      {original ? <pre className={styles.body} data-nc-report-source-body="">
        {highlight === null
          ? source.body
          : (
            <>
              {highlight.before}
              <mark ref={markRef} className={styles.quote} data-nc-report-source-quote={quoteId ?? ''}>
                {highlight.quote}
              </mark>
              {highlight.after}
            </>
          )}
      </pre> : <div className={styles.reading} data-nc-report-source-reading="">
        <ProseBlock markdown={source.body} blockId={null} destinationMode="inert" />
      </div>}
    </article>
  );
}

function Origin({ source }: { source: TrackSourceDetail }) {
  const { origin } = source;
  return (
    <>
      {origin.kind === 'plugin' && (
        <div className={styles.metaRow}>
          <dt className={styles.metaLabel}>{SOURCE_PANEL_COPY.origin}</dt>
          <dd className={`${styles.metaValue} ${styles.mono}`}>
            {SOURCE_PANEL_COPY.originPlugin} {origin.plugin_id} · {SOURCE_PANEL_COPY.originTool} {origin.tool}
          </dd>
        </div>
      )}
      {source.content_id !== undefined && source.content_id !== '' && (
        <div className={styles.metaRow}>
          <dt className={styles.metaLabel}>{SOURCE_PANEL_COPY.contentId}</dt>
          <dd className={`${styles.metaValue} ${styles.mono}`}>{source.content_id}</dd>
        </div>
      )}
      {source.url !== undefined && source.url !== '' && (
        <div className={styles.metaRow}>
          <dt className={styles.metaLabel}>{SOURCE_PANEL_COPY.url}</dt>
          {/* Text, not `<a href>`: the URL is planner-declared, so it steers nothing. */}
          <dd className={`${styles.metaValue} ${styles.mono}`}>{source.url}</dd>
        </div>
      )}
    </>
  );
}

/** The capture instant in the reader's locale; the raw RFC 3339 stays on `dateTime`. */
function capturedAtLabel(capturedAt: string): string {
  const instant = new Date(capturedAt);
  return Number.isNaN(instant.getTime()) ? capturedAt : instant.toLocaleString();
}
