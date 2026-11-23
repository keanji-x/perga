import { useMemo } from 'react';
import { Popover } from '@astryxdesign/core/Popover';
import { readErrorText } from '../../../../../core/domain/read-failure.ts';
import type { Track, TrackTemplate } from '../../../../../core/domain/track.ts';
import { EmptyState } from '../../../ui/empty-state/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import { useState } from '../../../ui/state/public.ts';
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
  onApplyTemplate?: (templateId: string) => Promise<void>;
}>;

const INLINE_TEMPLATE_LIMIT = 4;

export function ReportEmpty({
  track,
  lead,
  onOpenLead,
  templates,
  onApplyTemplate,
}: ReportEmptyProps = {}) {
  const [moreOpen, setMoreOpen] = useState(false);
  const [applyingId, setApplyingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const readyTemplates = (templates ?? []).filter(template => template.availability.status === 'ready');
  const canPickTemplate = onApplyTemplate !== undefined && readyTemplates.length > 0;

  const guideTrack = useMemo(
    () => (canPickTemplate ? buildGuideTrack(track, lead, readyTemplates.length) : null),
    [canPickTemplate, lead, readyTemplates.length, track],
  );
  const guideViews = useMemo(
    () => (canPickTemplate ? buildGuideViews(lead) : undefined),
    [canPickTemplate, lead],
  );

  const applyTemplate = async (templateId: string) => {
    if (onApplyTemplate === undefined || applyingId !== null) return;
    setApplyingId(templateId);
    setError(null);
    try {
      await onApplyTemplate(templateId);
      setMoreOpen(false);
    } catch (failure: unknown) {
      setError(readErrorText(failure, '套用模板失败，请稍后重试。'));
    } finally {
      setApplyingId(null);
    }
  };

  if (!canPickTemplate || guideTrack === null) {
    return <div className={styles.empty} data-nc-report-empty="">
      <EmptyState
        align="center"
        icon={<Icon name="file" />}
        title="进展会整理在这里"
        description="说说想做的事，计划与结论会随对话整理。"
      />
    </div>;
  }

  const featuredTemplates = readyTemplates.slice(0, INLINE_TEMPLATE_LIMIT);
  const overflowTemplates = readyTemplates.slice(INLINE_TEMPLATE_LIMIT);

  return <div className={styles.guide} data-nc-report-empty="">
    <TemplateProtocolBar track={guideTrack} />

    <ReportDocument
      report={GUIDE_REPORT}
      templateViews={guideViews}
      allowAutoFlow={false}
      empty={null}
      onOpenWorker={onOpenLead}
    />

    <div className={styles.picker}>
      <div className={styles.templateList} role="group" aria-label="可用模板">
        {featuredTemplates.map((template) => {
          const busy = applyingId === template.id;
          return (
            <button
              key={template.id}
              type="button"
              className={styles.templateRow}
              disabled={applyingId !== null}
              onClick={() => { void applyTemplate(template.id); }}
            >
              <span className={styles.rowBody}>
                <span className={styles.rowTitle}>{busy ? `正在套用 · ${template.title}…` : template.title}</span>
                {template.description != null && template.description.trim() !== '' && (
                  <span className={styles.rowDesc}>{template.description}</span>
                )}
              </span>
              <span className={styles.rowAction} aria-hidden="true">
                <span>{busy ? '套用中' : '套用模板'}</span>
                <Icon name="chevron-right" size="sm" />
              </span>
            </button>
          );
        })}
      </div>

      {overflowTemplates.length > 0 && (
        <div className={styles.overflowWrap}>
          <Popover
            label="更多模板"
            placement="below"
            alignment="start"
            width="24rem"
            hasCloseButton={false}
            className={styles.popover}
            isOpen={moreOpen}
            onOpenChange={(nextOpen) => {
              if (applyingId !== null) return;
              setMoreOpen(nextOpen);
            }}
            content={
              <div className={styles.popoverList} role="group" aria-label="更多可用模板">
                {overflowTemplates.map((template) => {
                  const busy = applyingId === template.id;
                  return (
                    <button
                      key={template.id}
                      type="button"
                      className={styles.templateRow}
                      disabled={applyingId !== null}
                      onClick={() => { void applyTemplate(template.id); }}
                    >
                      <span className={styles.rowBody}>
                        <span className={styles.rowTitle}>{busy ? `正在套用 · ${template.title}…` : template.title}</span>
                        {template.description != null && template.description.trim() !== '' && (
                          <span className={styles.rowDesc}>{template.description}</span>
                        )}
                      </span>
                      <span className={styles.rowAction} aria-hidden="true">
                        <span>{busy ? '套用中' : '套用模板'}</span>
                        <Icon name="chevron-right" size="sm" />
                      </span>
                    </button>
                  );
                })}
              </div>
            }
          >
            {(trigger) => (
              <button
                {...trigger}
                type="button"
                className={styles.moreTrigger}
                disabled={applyingId !== null}
              >
                <span>查看其余 {overflowTemplates.length} 份模板</span>
                <Icon name="chevron-right" size="sm" />
              </button>
            )}
          </Popover>
        </div>
      )}

      {error !== null && <p className={styles.error} role="alert">{error}</p>}
    </div>
  </div>;
}
