import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import type { TrackAggregateSnapshotDto } from "../../../../core/api/perga-v1.ts";
import { EmptyState } from '../../ui/empty-state/public.tsx';
import { Icon } from '../../ui/icon/public.tsx';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { readFailureOf,readFailureText } from '../../../../core/domain/read-failure.ts';
import type { ReportFileLinkTarget } from '../../../../core/domain/report-file.ts';
import type { ReportSourceLinkTarget } from '../../../../core/domain/report-source.ts';
import { readReportDocument,type ReportLinkTarget,type TrackReport } from '../../../../core/domain/report.ts';
import { toTrack,} from '../../../../core/domain/track.ts';
import { ReportDocument } from '../../features/report/document/public.tsx';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { createTrackWorkspaceFilesPort } from '../providers/directory.ts';
import { trackDetailQueryOptions } from '../providers/track-queries.ts';
import { ReportSourcePreview } from './report-source.tsx';
import { useTrackTemplateViews } from './report-views.ts';

type Navigation = Readonly<{
  onOpenLink: (target: ReportLinkTarget) => void;
  onOpenSource: (trackId: string, target: ReportSourceLinkTarget) => void;
  onOpenFile: (trackId: string, target: ReportFileLinkTarget) => void;
}>;

/** Mounted only while hovering. Reuse the route's authenticated query, decoder and
 * document renderer; each nested file/source remains scoped to its owning Track. */
export function ReportReferencePreview({ transport, unauthorized, target, ...navigation }: Navigation & Readonly<{
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  target: ReportLinkTarget;
}>) {
  const query = useQuery(trackDetailQueryOptions(transport, target.trackId, unauthorized));
  if (query.data !== undefined) {
    const report = readReportDocument(query.data.report);
    const allowAutoFlow = target.blockId === null;
    if (!allowAutoFlow && report === null) return <EmptyReportReference />;
    const block = report?.blocks?.find(candidate => candidate.id === target.blockId);
    if (!allowAutoFlow && block === undefined) return <p>报告中没有这条引用指向的内容。</p>;
    return <ReferenceDocument transport={transport} unauthorized={unauthorized} target={target}
      detail={query.data} report={block === undefined ? report
        : { summary: '', body: '', blocks: [block] }} allowAutoFlow={allowAutoFlow} {...navigation} />;
  }
  if (query.isError) return <ErrorBox message={readFailureText(readFailureOf(query.error), '无法读取报告。')}
    onRetry={() => { void query.refetch(); }} />;
  return <p role="status">正在读取报告…</p>;
}

function ReferenceDocument({ transport, unauthorized, target, detail, report: referencedReport, allowAutoFlow, onOpenLink, onOpenSource, onOpenFile }: Navigation & Readonly<{
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  target: ReportLinkTarget;
  detail: TrackAggregateSnapshotDto;
  report: TrackReport | null;
  allowAutoFlow: boolean;
}>) {
  const files = useMemo(() => createTrackWorkspaceFilesPort(transport, unauthorized, target.trackId),
    [transport, unauthorized, target.trackId]);
  const hasWidget = referencedReport?.blocks?.some((block) => block.kind === 'widget') ?? false;
  const templateViews = useTrackTemplateViews(transport, target.trackId, unauthorized, hasWidget || allowAutoFlow);
  const flow = templateViews?.status === 'ready' ? templateViews.projection.flow : null;
  const hasFlowContent = flow != null && (flow.nodes.length > 0 || flow.archived.length > 0);
  if (referencedReport === null && !hasFlowContent) return <EmptyReportReference />;

  const report = referencedReport ?? { summary: '', body: '', blocks: [] };
  return <ReportDocument report={report} allowAutoFlow={allowAutoFlow} empty={<EmptyReportReference />} fileRoot={toTrack(detail.track).agentCwd}
    onOpenLink={onOpenLink} onOpenSourceLink={source => onOpenSource(target.trackId, source)}
    onOpenFileLink={file => onOpenFile(target.trackId, file)}
    templateViews={templateViews}

    linkPreview={{ files, trackId: target.trackId, report,
      renderSource: source => <ReportSourcePreview transport={transport} unauthorized={unauthorized} trackId={target.trackId} target={source} />,
      renderReference: reference => <ReportReferencePreview transport={transport} unauthorized={unauthorized} target={reference}
        onOpenLink={onOpenLink} onOpenSource={onOpenSource} onOpenFile={onOpenFile} />,
    }} />;
}

function EmptyReportReference() {
  return <EmptyState variant="compact" icon={<Icon name="file" />} title="这份报告暂无内容。" description="报告有内容后，可以在这里预览。" />;
}
