import { Icon } from '../../../ui/icon/public.tsx';
import { EmptyState } from '../../../ui/empty-state/public.tsx';
import { useCompactViewport } from '../../../ui/viewport/public.ts';
// The report document — the main column on a track and an area: a sequence of typed blocks.

import { useEffect, useLayoutEffect, useMemo, useRef, type ReactNode } from 'react';
import { useState } from '../../../ui/state/public.ts';

import { PreparedProseBlock, PlainReportText, ProseBlock } from './content.tsx';
export { ProseBlock, ReportTextLink } from './content.tsx';
import type { ReferenceRepository } from '../../../../../core/domain/report-references.ts';
import { deriveReportTasks, isTaskBlock, type ReportBlock, type ReportLinkTarget, type ReportTaskRow, type TrackReport } from '../../../../../core/domain/report.ts';
import { type PreviewResolution } from '../../../../../core/domain/report-backlinks.ts';
import { type TaskVerdict } from '../../../../../core/domain/report-verdicts.ts';
import { type TemplateViewsProjection, type TemplateViewsState } from '../../../../../core/domain/report-widget.ts';
import type { FlowProjection } from '../../../../../core/domain/report-flow.ts';
import {
  type ReportFileLinkTarget,
} from '../../../../../core/domain/report-file.ts';
import type { SeriesResolution } from '../../../../../core/domain/report-series.ts';
import { type ReportSourceLinkTarget } from '../../../../../core/domain/report-source.ts';
import { type ReportLinkPreviewResources } from '../link-preview/public.tsx';
import type { ReportSourceInsertion } from '../source/placement.ts';
import { prepareReportDocument, type PreparedReport, type PreparedReportBlock } from './preparation.ts';
import { ReportDetails } from './details.tsx';
import { REPORT_ANCHOR_REQUEST, revealReportAnchor, type ReportAnchorRequest } from '../anchor/public.ts';
import { ReportAppBlock } from '../app/public.tsx';
import { ReportCheckpointsBlock } from '../checkpoints/public.tsx';
import { ReportFlowBlock } from '../flow/public.tsx';
import { ReportWidgetBlock } from '../widget/public.tsx';
import { ReportHtmlBlock } from '../html/public.tsx';
import { ReportCandlesBlock } from '../candles/public.tsx';
import { ReportPreviewBlock, type PreviewViewportStore } from '../preview/public.tsx';
import { ReportSeriesBlock } from '../series/public.tsx';
import { ReportObjectiveBlock } from '../objective/public.tsx';
import { ReportTableBlock } from '../table/public.tsx';
import { NativeReportView } from '../native/public.tsx';
import { ReportTaskBlock } from '../task/public.tsx';
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
  /** A `neige://wave/…` link was activated. Absent ⇒ links render as plain text. */
  onOpenLink?: (target: ReportLinkTarget) => void;
  /** A file link admitted beneath `fileRoot` was activated. */
  onOpenFileLink?: (target: ReportFileLinkTarget) => void;
  /** Opens a Worker conversation bound to a report checkpoint or task. */
  onOpenWorker?: (sessionId: string) => void;
  /** A `neige://source/…` citation was activated. Absent ⇒ citations render as an inline badge plus their label. */
  onOpenSourceLink?: (target: ReportSourceLinkTarget) => void;
  /** Absolute root used to admit relative or already-absolute workspace links. */
  fileRoot?: string;
  /** Workspace-relative directory containing the Markdown currently rendered. */
  fileBasePath?: string;
  linkPreview?: ReportLinkPreviewResources;
  /** The anchor the reader arrived at, from a deep link or a backlink. */
  arrivalAnchorId?: string | null;
  /** The same execution diagnostics used by the task inventory. */
  taskVerdicts?: readonly TaskVerdict[];
  /** App-composed rows shared with the task inventory when execution evidence is loaded. */
  taskRows?: readonly ReportTaskRow[];
  /** App-owned current/history query, scoped to a task. */
  renderTaskExecution?: (task: ReportTaskRow, expanded: boolean) => ReactNode;
  /** Resolves a Track overlay for a live `table` or a `view` live slot; each renderer validates its declared contract. */
  resolveOverlay?: (source: string) => unknown;
  /** Projected template views & bundled HTML widgets for Scheme B `widget` blocks. */
  templateViews?: TemplateViewsProjection | TemplateViewsState;
  /** Whether to auto-inject `builtin:flow` when the Track has flow nodes but no explicit flow widget. Defaults to true. */
  allowAutoFlow?: boolean;
  /** Resolves a `chart.series` block, by id and rev, to the app's query of the kernel's resolved data. Absent ⇒ series blocks say the view carries no such data. */
  resolveSeries?: (blockId: string, rev: number) => SeriesResolution | undefined;
  /** Resolves a `preview` block's `key` to the app's read of the track's registered previews. Absent ⇒ preview blocks say the view carries none. */
  resolvePreview?: (key: string) => PreviewResolution;
  /** Where preview blocks remember the reader's device choice, by block key (the app scopes it per track). Absent ⇒ not remembered. */
  previewViewports?: PreviewViewportStore;
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
  resolveOverlay, templateViews, allowAutoFlow = true, resolveSeries, resolvePreview, previewViewports,
  arrivalAnchorId, taskVerdicts, taskRows, renderTaskExecution,
}: ReportDocumentProps) {
  const compactViewport = useCompactViewport();
  const [widgetSelections, setWidgetSelections] = useState<Readonly<Record<string, string>>>({});
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
            taskRows={taskRows}
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
      <article className={`calm-prose ${styles.doc}`} data-nc-report="" tabIndex={-1}>
        {rail}
        {byline !== undefined && <div className={styles.byline}>{byline}</div>}
        {autoFlowNode}
      </article>
    );
  }
  if (prepared.outlineOnly && outlineEmpty !== undefined && autoFlowNode === null) return <>{outlineEmpty}</>;
  const outlineOnly = compactViewport && prepared.outlineOnly && autoFlowNode === null;

  return (
    <article className={`calm-prose ${styles.doc} ${outlineOnly ? styles.outlineOnly : ''}`} data-nc-report="" tabIndex={-1}>
      {outlineOnly && <div className={styles.mobileEmpty}><EmptyState icon={<Icon name="file" />} title="进展会整理在这里" description="说说想做的事，计划与结论会随对话整理。" /></div>}
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
          const documentBlocks = prepared.blocks.filter(slot => !isProcessBlock(slot.block));
          const processBlocks = prepared.blocks.map(slot => slot.block).filter(isProcessBlock);
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
              resolveOverlay={resolveOverlay}
              templateViews={templateViews}
              widgetSelections={widgetSelections}
              onSelectWidget={(key, value) => {
                setWidgetSelections((prev) => (prev[key] === value ? prev : { ...prev, [key]: value }));
              }}
              resolveSeries={resolveSeries}
              resolvePreview={resolvePreview}
              previewViewports={previewViewports}
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
              {processBlocks.length > 0 && (
                <ReportReference blocks={processBlocks} backlinkCounts={backlinkCounts}
                  sourceInsertion={sourceInsertion}
                  tasks={taskRows} verdicts={taskVerdicts} renderTaskExecution={renderTaskExecution}
                  referenceRepository={referenceRepository} onOpenLink={onOpenLink} onOpenFileLink={onOpenFileLink}
                  onOpenSourceLink={onOpenSourceLink} fileRoot={fileRoot} fileBasePath={fileBasePath} linkPreview={linkPreview} />
              )}
              {sourceInsertion !== undefined && !prepared.blocks.some(({ block }) => block.id === sourceInsertion.position?.blockId)
                && <div className={styles.block}>{sourceInsertion.content}</div>}
            </>
          );
        })()}
    </article>
  );
}

function isProcessBlock(block: ReportBlock): boolean {
  return isTaskBlock(block);
}

function ReportReference({ blocks, backlinkCounts, tasks, verdicts, renderTaskExecution, sourceInsertion, ...textContext }: {
  blocks: readonly ReportBlock[];
  backlinkCounts?: ReadonlyMap<string, number>;
  tasks?: readonly ReportTaskRow[];
  verdicts?: readonly TaskVerdict[];
  referenceRepository: ReferenceRepository | null;
  onOpenLink?: (target: ReportLinkTarget) => void;
  onOpenFileLink?: (target: ReportFileLinkTarget) => void;
  onOpenSourceLink?: (target: ReportSourceLinkTarget) => void;
  fileRoot?: string;
  fileBasePath?: string;
  linkPreview?: ReportLinkPreviewResources;
  renderTaskExecution?: ReportDocumentProps['renderTaskExecution'];
  sourceInsertion?: ReportSourceInsertion;
}) {
  const [visited, setVisited] = useState(false);
  const region = useRef<HTMLDivElement | null>(null);
  const pendingReveal = useRef<(() => void) | null>(null);
  useLayoutEffect(() => {
    const element = region.current;
    if (element === null || visited) return;
    const prepare = (event: Event) => {
      if (!(event.target instanceof HTMLElement) || !blocks.some(block => block.id === (event.target as HTMLElement).id)) return;
      event.preventDefault();
      pendingReveal.current = (event as ReportAnchorRequest).detail.resume;
      setVisited(true);
    };
    element.addEventListener(REPORT_ANCHOR_REQUEST, prepare);
    return () => { element.removeEventListener(REPORT_ANCHOR_REQUEST, prepare); };
  }, [blocks, setVisited, visited]);
  useLayoutEffect(() => {
    if (!visited) return;
    const resume = pendingReveal.current;
    pendingReveal.current = null;
    resume?.();
  }, [visited]);
  const rows = visited ? tasks ?? deriveReportTasks(blocks, verdicts) : [];
  const tasksByBlock = new Map(rows.map((task) => [task.blockId, task]));
  return (
    <div className={styles.row} ref={region}>
      <ReportDetails title="Reference" meta={`${blocks.length} ${blocks.length === 1 ? 'task' : 'tasks'}`} layout="grid" reference
        onToggle={open => { if (open) setVisited(true); }}>
        {/* Not `BlockSlot`: a slot is `display: contents` over the article's grid, and this `<details>` is not that grid. */}
        {blocks.map((block) => {
          const backlinks = backlinkCounts?.get(block.id) ?? 0;
          return (
            <div key={block.id} className={styles.referenceItem} id={block.id} data-nc-report-source-block={block.id}>
              {visited && <BlockBody block={block} task={tasksByBlock.get(block.id)} renderTaskExecution={renderTaskExecution} {...textContext} />}
              {sourceInsertion?.position?.blockId === block.id && sourceInsertion.content}
              {backlinks > 0 && (
                <span
                  className={styles.referenceSidenote}
                  title={`${backlinks} report${backlinks === 1 ? '' : 's'} cite this block`}
                >
                  ◂ {backlinks}
                </span>
              )}
            </div>
          );
        })}
      </ReportDetails>
    </div>
  );
}

/** One block, plus the sidenote that belongs to it. Reports are read-only publications. */
function BlockSlot({
  slot, backlinks, onOpenLink, onOpenFileLink, onOpenWorker, taskRows, onOpenSourceLink, fileRoot, fileBasePath, linkPreview, referenceRepository, resolveOverlay, templateViews, widgetSelections, onSelectWidget, resolveSeries,
  resolvePreview, previewViewports, sourceInsertion,
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
  resolveOverlay?: ReportDocumentProps['resolveOverlay'];
  templateViews?: ReportDocumentProps['templateViews'];
  widgetSelections?: Readonly<Record<string, string>>;
  onSelectWidget?: (key: string, value: string) => void;
  resolveSeries?: ReportDocumentProps['resolveSeries'];
  resolvePreview?: ReportDocumentProps['resolvePreview'];
  previewViewports?: PreviewViewportStore;
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
              onOpenSourceLink={onOpenSourceLink} linkPreview={linkPreview}
              resolveOverlay={resolveOverlay} templateViews={templateViews} widgetSelections={widgetSelections} onSelectWidget={onSelectWidget} resolveSeries={resolveSeries} resolvePreview={resolvePreview}
              previewViewports={previewViewports} />}
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
  block, task, taskRows, renderTaskExecution, referenceRepository, onOpenLink, onOpenFileLink, onOpenWorker, fileRoot, fileBasePath, onOpenSourceLink, linkPreview, resolveOverlay, templateViews, widgetSelections, onSelectWidget, resolveSeries, resolvePreview, previewViewports,
}: {
  referenceRepository: ReferenceRepository | null;
  onOpenLink?: (target: ReportLinkTarget) => void;
  onOpenFileLink?: (target: ReportFileLinkTarget) => void;
  onOpenWorker?: (sessionId: string) => void;
  fileRoot?: string;
  fileBasePath?: string;
  block: ReportBlock; task?: ReportTaskRow; taskRows?: readonly ReportTaskRow[]; renderTaskExecution?: ReportDocumentProps['renderTaskExecution'];
  /** A table cell that is one source citation is the same control the prose paints. */
  onOpenSourceLink?: (target: ReportSourceLinkTarget) => void;
  linkPreview?: ReportLinkPreviewResources;
  resolveOverlay?: ReportDocumentProps['resolveOverlay'];
  templateViews?: ReportDocumentProps['templateViews'];
  widgetSelections?: Readonly<Record<string, string>>;
  onSelectWidget?: (key: string, value: string) => void;
  resolveSeries?: ReportDocumentProps['resolveSeries'];
  resolvePreview?: ReportDocumentProps['resolvePreview'];
  previewViewports?: PreviewViewportStore;
}): ReactNode {
  const textContext = { referenceRepository, onOpenLink, onOpenFileLink, onOpenSourceLink, fileRoot, fileBasePath, linkPreview };
  const renderText = (text: string) => <PlainReportText text={text} context={textContext} />;
  switch (block.kind) {
    case 'objective': return <ReportObjectiveBlock payload={block.payload} />;
    case 'table':
      return <ReportTableBlock renderText={renderText} payload={block.payload} resolveLive={resolveOverlay} onOpenSourceLink={onOpenSourceLink} linkPreview={linkPreview} />;
    case 'view': return <NativeReportView renderText={renderText} payload={block.payload} resolveOverlay={resolveOverlay} onOpenSourceLink={onOpenSourceLink} linkPreview={linkPreview} />;
    case 'chart.candles': return <ReportCandlesBlock payload={block.payload} />;
    case 'chart.series':
      return <ReportSeriesBlock payload={block.payload} blockId={block.id} rev={block.rev} resolve={resolveSeries} />;
    case 'task': return <ReportTaskBlock payload={block.payload} blockId={block.id} task={task} renderExecution={renderTaskExecution}
      textContext={textContext} />;
    case 'app': return <ReportAppBlock payload={block.payload} />;
    case 'preview': return <ReportPreviewBlock payload={block.payload} resolve={resolvePreview}
      viewports={previewViewports} />;
    /* The page is read through the same Track file port as prose links and images. */
    case 'html': return <ReportHtmlBlock payload={block.payload} files={linkPreview?.files}
      onOpen={onOpenFileLink === undefined ? undefined : (path) => { onOpenFileLink({ path }); }} />;
    case 'checkpoints':
      return (
        <ReportCheckpointsBlock
          payload={block.payload}
          taskRows={taskRows}
          renderText={renderText}
          renderMarkdown={(markdown, key) => (
            <ProseBlock markdown={markdown} blockId={key} compact {...textContext} />
          )}
          onOpenWorker={onOpenWorker}
        />
      );
    case 'widget':
      return (
        <ReportWidgetBlock
          payload={block.payload}
          projection={templateViews}
          taskRows={taskRows}
          selectedId={block.payload.selectionKey !== undefined ? widgetSelections?.[block.payload.selectionKey] : undefined}
          onSelect={onSelectWidget}
          renderText={renderText}
          renderMarkdown={(markdown, key) => (
            <ProseBlock markdown={markdown} blockId={key} compact {...textContext} />
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
