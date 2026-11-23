import { useMemo } from 'react';
import type { Track, TrackTemplate } from '../../../../../core/domain/track.ts';
import { EmptyState } from '../../../ui/empty-state/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import { ReportDocument } from '../document/public.tsx';
import { TemplateProtocolBar } from '../template-protocol/public.tsx';
import { buildGuideTrack, buildGuideViews, GUIDE_REPORT, type ReportEmptyLeadState } from './guide.ts';
import styles from './empty.module.css';

export type { ReportEmptyLeadPhase, ReportEmptyLeadState } from './guide.ts';

type ReportEmptyProps = Readonly<{
  track?: Track;
  lead?: ReportEmptyLeadState;
  onOpenLead?: (sessionId: string) => void;
  templates?: readonly TrackTemplate[];
}>;

export function ReportEmpty({
  track,
  lead,
  onOpenLead,
  templates,
}: ReportEmptyProps = {}) {
  const readyCount = (templates ?? []).filter(template => template.availability.status === 'ready').length;
  const showGuide = readyCount > 0;

  const guideTrack = useMemo(
    () => (showGuide ? buildGuideTrack(track, lead, readyCount) : null),
    [lead, readyCount, showGuide, track],
  );
  const guideViews = useMemo(
    () => (showGuide ? buildGuideViews(lead) : undefined),
    [lead, showGuide],
  );

  if (!showGuide || guideTrack === null) {
    return <div className={styles.empty} data-nc-report-empty="">
      <EmptyState
        align="center"
        icon={<Icon name="file" />}
        title="进展会整理在这里"
        description="说说想做的事，计划与结论会随对话整理。"
      />
    </div>;
  }

  return <div className={styles.guide} data-nc-report-empty="">
    <TemplateProtocolBar track={guideTrack} />

    <ReportDocument
      report={GUIDE_REPORT}
      templateViews={guideViews}
      allowAutoFlow={false}
      empty={null}
      onOpenWorker={onOpenLead}
    />
  </div>;
}
