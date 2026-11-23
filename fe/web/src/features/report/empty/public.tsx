import { Button } from '@astryxdesign/core/Button';
import { Popover } from '@astryxdesign/core/Popover';
import type { TrackTemplate } from '../../../../../core/domain/track.js';
import { readErrorText } from '../../../../../core/domain/read-failure.js';
import { EmptyState } from '../../../ui/empty-state/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import styles from './empty.module.css';

type ReportEmptyProps = Readonly<{
  templates?: readonly TrackTemplate[];
  onApplyTemplate?: (templateId: string) => Promise<void>;
}>;

function formatTemplateIndex(index: number): string {
  return String(index + 1).padStart(2, '0');
}

export function ReportEmpty({ templates, onApplyTemplate }: ReportEmptyProps = {}) {
  const [open, setOpen] = useState(false);
  const [applyingId, setApplyingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const readyTemplates = (templates ?? []).filter(template => template.availability.status === 'ready');
  const canPickTemplate = onApplyTemplate !== undefined && readyTemplates.length > 0;
  const triggerLabel = applyingId !== null ? '正在套用…' : '选择模板';

  const applyTemplate = async (templateId: string) => {
    if (onApplyTemplate === undefined || applyingId !== null) return;
    setApplyingId(templateId);
    setError(null);
    try {
      await onApplyTemplate(templateId);
      setOpen(false);
    } catch (failure: unknown) {
      setError(readErrorText(failure, '套用模板失败，请稍后重试。'));
    } finally {
      setApplyingId(null);
    }
  };

  return <div className={canPickTemplate ? `${styles.empty} ${styles.editorial}` : styles.empty} data-nc-report-empty="">
    <EmptyState
      align={canPickTemplate ? 'start' : 'center'}
      icon={<Icon name="file" />}
      title="进展会整理在这里"
      description={canPickTemplate
        ? '说说想做的事，计划、研究与结论会随对话整理到这里。'
        : '说说想做的事，计划与结论会随对话整理。'}
    />
    {canPickTemplate && (
      <div className={styles.action}>
        <Popover
          label="选择模板"
          placement="below"
          alignment="start"
          width="24rem"
          hasCloseButton={false}
          className={styles.popover}
          isOpen={open}
          onOpenChange={(nextOpen) => {
            if (applyingId !== null) return;
            setOpen(nextOpen);
            if (!nextOpen) setError(null);
          }}
          content={
            <div className={styles.panel}>
              <div className={styles.panelHeader}>
                <span className={styles.panelEyebrow}>从模板开始</span>
                <span className={styles.panelCount}>{formatTemplateIndex(readyTemplates.length - 1)}</span>
              </div>
              <div className={styles.list} role="group" aria-label="可用模板">
                {readyTemplates.map((template, index) => {
                  const busy = applyingId === template.id;
                  return (
                    <button
                      key={template.id}
                      type="button"
                      className={styles.option}
                      disabled={applyingId !== null}
                      onClick={() => { void applyTemplate(template.id); }}
                    >
                      <span className={styles.optionIndex} aria-hidden="true">{formatTemplateIndex(index)}</span>
                      <span className={styles.optionBody}>
                        <span className={styles.optionTitle}>{busy ? `正在套用 ${template.title}…` : template.title}</span>
                        {template.description != null && template.description.trim() !== '' && (
                          <span className={styles.optionDesc}>{template.description}</span>
                        )}
                      </span>
                      <span className={styles.optionArrow} aria-hidden="true">
                        <Icon name="chevron-right" size="sm" />
                      </span>
                    </button>
                  );
                })}
              </div>
              {error !== null && <p className={styles.error} role="alert">{error}</p>}
            </div>
          }
        >
          {(trigger) => (
            <Button
              {...trigger}
              size="sm"
              variant="ghost"
              label={triggerLabel}
              className={styles.trigger}
              data-open={open || undefined}
              isDisabled={applyingId !== null}
            >
              <span className={styles.triggerContent}>
                <span className={styles.triggerGlyph} aria-hidden="true">
                  <Icon name="plus" size="sm" />
                </span>
                <span>{triggerLabel}</span>
                <span className={styles.triggerChevron} data-open={open || undefined} aria-hidden="true">
                  <Icon name="chevron-right" size="sm" />
                </span>
              </span>
            </Button>
          )}
        </Popover>
      </div>
    )}
  </div>;
}
