import { Icon } from '../../../ui/icon/public.tsx';
import { EmptyState } from '../../../ui/empty-state/public.tsx';
import { useCompactViewport } from '../../../ui/viewport/public.ts';
// The report document — the main column on a track and an area: a sequence of typed blocks.

import { useEffect, useMemo, type ReactNode } from 'react';

import { PreparedProseBlock, PlainReportText, ProseBlock } from './content.tsx';
export { ProseBlock, ReportTextLink } from './content.tsx';
import type { ReferenceRepository } from '../../../../../core/domain/report-references.ts';
import { type ReportBlock, type ReportLinkTarget, type TrackReport } from '../../../../../core/domain/report.ts';
import { type ReportTaskRow } from '../../../../../core/domain/track-task-inventory.ts';
import { type TemplateViewsProjection, type TemplateViewsState } from '../../../../../core/domain/report-widget.ts';
import type { FlowProjection } from '../../../../../core/domain/report-flow.ts';
import {
  type ReportFileLinkTarget,
} from '../../../../../core/domain/report-file.ts';
import { type ReportSourceLinkTarget } from '../../../../../core/domain/report-source.ts';
import { MESSAGES } from '../../../../../core/domain/messages.ts';
import { type ReportLinkPreviewResources } from '../link-preview/public.tsx';
import type { ReportSourceInsertion } from '../source/placement.ts';
import { prepareReportDocument, type PreparedReport, type PreparedReportBlock } from './preparation.ts';
import { revealReportAnchor } from '../anchor/public.ts';
import { ReportFlowBlock } from '../flow/public.tsx';
import { ReportWidgetBlock } from '../widget/public.tsx';
import styles from './document.module.css';

export type ReportDocumentProps = Readonly<{
  /** The track's report. `null` when it has none. */
  report: TrackReport | null;
  /** Prepared by the report owner; when supplied, it must describe this exact immutable report. */
  preparation?: PreparedReport;
  /** What the empty state should offer, which differs per route. */
  empty: ReactNode;
  /** Owning page presentation for a heading-only report. */
  outlineEmpty?: ReactNode;
  /** The outline rail, which hangs in the document's leading gutter. */
  rail?: ReactNode;
  /** The byline row (`Planner Agent · 2h`), composed by `app/router`. */
  byline?: ReactNode;
  /** The open citation and its position in this document. */
  sourceInsertion?: ReportSourceInsertion;
  /** How many backlinks land on each block, for the sidenote markers. */
  backlinkCounts?: ReadonlyMap<string, number>;
  /** A `perga://wave/…` link was activated. Absent ⇒ links render as plain text. */
  onOpenLink?: (target: ReportLinkTarget) => void;
  /** A file link admitted beneath `fileRoot` was activated. */
  onOpenFileLink?: (target: ReportFileLinkTarget) => void;
  /** Opens a Worker conversation linked from a Report widget. */
  onOpenWorker?: (sessionId: string) => void;
  /** A `perga://source/…` citation was activated. Absent ⇒ citations render as an inline badge plus their label. */
  onOpenSourceLink?: (target: ReportSourceLinkTarget) => void;
  /** Absolute root used to admit relative or already-absolute workspace links. */
  fileRoot?: string;
  /** Workspace-relative directory containing the Markdown currently rendered. */
  fileBasePath?: string;
  linkPreview?: ReportLinkPreviewResources;
  /** The anchor the reader arrived at, from a deep link or a backlink. */
  arrivalAnchorId?: string | null;
  /** App-composed rows shared with the task inventory when execution evidence is loaded. */
  taskRows?: readonly ReportTaskRow[];
  /** Projected template views & bundled HTML widgets for Scheme B `widget` blocks. */
  templateViews?: TemplateViewsProjection | TemplateViewsState;
  /** Whether to auto-inject `builtin:flow` when the Track has flow nodes but no explicit flow widget. Defaults to true. */
  allowAutoFlow?: boolean;
  /** Whether this document represents a committed Track report (`data-nc-report`). Defaults to true. */
  authored?: boolean;
}>;

function resolveAutoFlow(
  templateViews: TemplateViewsProjection | TemplateViewsState | undefined,
  blocks: readonly ReportBlock[] | null,
): FlowProjection | null {
  if (templateViews === undefined) return null;
  const projection = 'status' in templateViews
    ? (templateViews.status === 'ready' ? templateViews.projection : null)
    : templateViews;
  if (projection === null) return null;
  const flow = projection.flow;
  if (flow == null || (flow.nodes.length === 0 && flow.archived.length === 0)) return null;
  const hasExplicitFlowWidget = (blocks ?? []).some(
    (block) => block.kind === 'widget' && block.payload.use === 'builtin:flow',
  );
  return hasExplicitFlowWidget ? null : flow;
}

function isTargetSectionProse(slot: PreparedReportBlock | undefined): boolean {
  if (slot === undefined || slot.block.kind !== 'prose') return false;
  return /^\s*#\s+(?:target|目标)(?=$|\s)/im.test(slot.block.payload.markdown);
}

/** Agent-authored destinations become admitted preview controls. Navigation stays behind typed callbacks or explicit external activation. */
export function ReportDocument({
  report, preparation, empty, outlineEmpty, rail, byline, sourceInsertion, backlinkCounts, onOpenLink, onOpenFileLink, onOpenWorker, onOpenSourceLink, fileRoot, fileBasePath, linkPreview,
  templateViews, allowAutoFlow = true, authored = true, arrivalAnchorId, taskRows,
}: ReportDocumentProps) {
  const compactViewport = useCompactViewport();
  const prepared = useMemo(() => preparation ?? prepareReportDocument(report), [preparation, report]);
  if (prepared.report !== report) throw new Error('Report preparation does not match the displayed report');
  const { referenceRepository } = prepared;
  useEffect(() => {
    if (arrivalAnchorId === null || arrivalAnchorId === undefined) return;
    revealReportAnchor(arrivalAnchorId);
  }, [arrivalAnchorId]);

  const autoFlow = allowAutoFlow ? resolveAutoFlow(templateViews, report?.blocks ?? null) : null;
  const autoFlowNode = autoFlow === null ? null : (() => {
    const textContext = { referenceRepository, onOpenLink, onOpenFileLink, onOpenSourceLink, fileRoot, fileBasePath, linkPreview };
    return (
      <div className={styles.row} data-nc-auto-flow="">
        <div className={styles.block} data-nc-report-reading="" data-nc-link-reading="">
          <ReportFlowBlock
            flow={autoFlow}
            renderText={(text) => <PlainReportText text={text} context={textContext} />}
            renderMarkdown={(markdown, key) => (
              <ProseBlock markdown={markdown} blockId={key} compact {...textContext} />
            )}
            onOpenWorker={onOpenWorker}
          />
        </div>
      </div>
    );
  })();

  if (prepared.kind === 'empty') {
    if (autoFlowNode === null) return <>{empty}</>;
    return (
      <article className={`calm-prose ${styles.doc}`} data-nc-report={authored ? '' : undefined} tabIndex={-1}>
        {rail}
        {byline !== undefined && <div className={styles.byline}>{byline}</div>}
        {autoFlowNode}
      </article>
    );
  }
  if (prepared.outlineOnly && outlineEmpty !== undefined && autoFlowNode === null) return <>{outlineEmpty}</>;
  const outlineOnly = compactViewport && prepared.outlineOnly && autoFlowNode === null;

  return (
    <article className={`calm-prose ${styles.doc} ${outlineOnly ? styles.outlineOnly : ''}`} data-nc-report={authored ? '' : undefined} tabIndex={-1}>
      {outlineOnly && <div className={styles.mobileEmpty}><EmptyState icon={<Icon name="file" />} title={MESSAGES.track.reportEmptyTitle} description={MESSAGES.track.reportEmptyDescription} /></div>}
      {rail}
      {byline !== undefined && <div className={styles.byline}>{byline}</div>}
      {prepared.kind === 'legacy'
        ? (
          <>
            {autoFlowNode}
            <div className={styles.row}>
              <div className={styles.block} data-nc-report-reading="" data-nc-link-reading="" data-nc-report-source-block="">
                <PreparedProseBlock
                  prepared={prepared.prose}
                  sourceInsertion={sourceInsertion}
                  referenceRepository={referenceRepository} onOpenLink={onOpenLink}
                  onOpenFileLink={onOpenFileLink}
                  onOpenSourceLink={onOpenSourceLink}
                  fileRoot={fileRoot}
                  fileBasePath={fileBasePath}
                  linkPreview={linkPreview}
                />
              </div>
            </div>
          </>
        )
        : (() => {
          const documentBlocks = prepared.blocks;
          const insertFlowAfterFirst = autoFlowNode !== null && isTargetSectionProse(documentBlocks[0]);
          const renderSlot = (slot: PreparedReportBlock) => (
            <BlockSlot
              key={slot.block.id}
              slot={slot}
              sourceInsertion={sourceInsertion?.position?.blockId === slot.block.id ? sourceInsertion : undefined}
              backlinks={backlinkCounts?.get(slot.block.id) ?? 0}
              referenceRepository={referenceRepository} onOpenLink={onOpenLink}
              onOpenFileLink={onOpenFileLink}
              onOpenWorker={onOpenWorker}
              taskRows={taskRows}
              onOpenSourceLink={onOpenSourceLink}
              fileRoot={fileRoot}
              fileBasePath={fileBasePath}
              linkPreview={linkPreview}
              templateViews={templateViews}
            />
          );
          return (
            <>
              {autoFlowNode !== null && !insertFlowAfterFirst && autoFlowNode}
              {insertFlowAfterFirst ? (
                <>
                  {renderSlot(documentBlocks[0])}
                  {autoFlowNode}
                  {documentBlocks.slice(1).map(renderSlot)}
                </>
              ) : documentBlocks.map(renderSlot)}
              {sourceInsertion !== undefined && !prepared.blocks.some(({ block }) => block.id === sourceInsertion.position?.blockId)
                && <div className={styles.block}>{sourceInsertion.content}</div>}
            </>
          );
        })()}
    </article>
  );
}

/** One block, plus the sidenote that belongs to it. Reports are read-only publications. */
function BlockSlot({
  slot, backlinks, onOpenLink, onOpenFileLink, onOpenWorker, taskRows, onOpenSourceLink, fileRoot, fileBasePath, linkPreview, referenceRepository, templateViews, sourceInsertion,
}: {
  referenceRepository: ReferenceRepository | null;
  slot: PreparedReportBlock;
  backlinks: number;
  onOpenLink?: (target: ReportLinkTarget) => void;
  onOpenFileLink?: (target: ReportFileLinkTarget) => void;
  onOpenWorker?: (sessionId: string) => void;
  taskRows?: readonly ReportTaskRow[];
  onOpenSourceLink?: (target: ReportSourceLinkTarget) => void;
  fileRoot?: string;
  fileBasePath?: string;
  linkPreview?: ReportLinkPreviewResources;
  templateViews?: ReportDocumentProps['templateViews'];
  sourceInsertion?: ReportSourceInsertion;
}) {
  const { block, prose } = slot;
  return (
    <div className={styles.row}>
      <div className={styles.block} id={block.id} data-nc-report-reading="" data-nc-link-reading="" data-nc-report-source-block={block.id}>
        {prose !== null
          ? (
            <PreparedProseBlock
              prepared={prose}
              sourceInsertion={sourceInsertion}
              referenceRepository={referenceRepository} onOpenLink={onOpenLink}
              onOpenFileLink={onOpenFileLink}
              onOpenSourceLink={onOpenSourceLink}
              fileRoot={fileRoot}
              fileBasePath={fileBasePath}
              linkPreview={linkPreview}
            />
          )
          : <BlockBody block={block} taskRows={taskRows} referenceRepository={referenceRepository} onOpenLink={onOpenLink}
              onOpenFileLink={onOpenFileLink} onOpenWorker={onOpenWorker} fileRoot={fileRoot} fileBasePath={fileBasePath}
              onOpenSourceLink={onOpenSourceLink} linkPreview={linkPreview} templateViews={templateViews} />}
        {!prose && sourceInsertion?.content}
      </div>
      {backlinks > 0 && (
        <span className={styles.sidenote} title={`${backlinks} report${backlinks === 1 ? '' : 's'} cite this block`}>
          ◂ {backlinks}
        </span>
      )}
    </div>
  );
}

/** One bad block may not cost the page: an unknown kind or an unparsable payload degrades to one line. */
function BlockBody({
  block, taskRows, referenceRepository, onOpenLink, onOpenFileLink, onOpenWorker, fileRoot, fileBasePath, onOpenSourceLink, linkPreview, templateViews,
}: {
  referenceRepository: ReferenceRepository | null;
  onOpenLink?: (target: ReportLinkTarget) => void;
  onOpenFileLink?: (target: ReportFileLinkTarget) => void;
  onOpenWorker?: (sessionId: string) => void;
  fileRoot?: string;
  fileBasePath?: string;
  block: ReportBlock;
  taskRows?: readonly ReportTaskRow[];
  /** A table cell that is one source citation is the same control the prose paints. */
  onOpenSourceLink?: (target: ReportSourceLinkTarget) => void;
  linkPreview?: ReportLinkPreviewResources;
  templateViews?: ReportDocumentProps['templateViews'];
}): ReactNode {
  const textContext = { referenceRepository, onOpenLink, onOpenFileLink, onOpenSourceLink, fileRoot, fileBasePath, linkPreview };
  const renderText = (text: string) => <PlainReportText text={text} context={textContext} />;
  switch (block.kind) {
    case 'widget':
      return (
        <ReportWidgetBlock
          payload={block.payload}
          projection={templateViews}
          taskRows={taskRows}
          renderText={renderText}
          renderMarkdown={(markdown, key) => (
            <ProseBlock markdown={markdown} blockId={`${block.id}-${key}`} compact {...textContext} />
          )}
          onOpenFile={onOpenFileLink === undefined ? undefined : (path) => { onOpenFileLink({ path }); }}
          onOpenWorker={onOpenWorker}
        />
      );
    case 'unsupported':
      return (
        <div className={styles.unsupported} role="note">
          unsupported block kind {block.declaredKind}
        </div>
      );
    // `prose` is handled by the slot, which owns the markdown pipeline.
    case 'prose': return null;
  }
}
