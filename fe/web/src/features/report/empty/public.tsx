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

export function ReportEmpty({ templates, onApplyTemplate }: ReportEmptyProps = {}) {
  const [open, setOpen] = useState(false);
  const [applyingId, setApplyingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const readyTemplates = (templates ?? []).filter(template => template.availability.status === 'ready');
  const canPickTemplate = onApplyTemplate !== undefined && readyTemplates.length > 0;

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

  return <div className={styles.empty} data-nc-report-empty="">
    <EmptyState
      align="center"
      icon={<Icon name="file" />}
      title="进展会整理在这里"
      description={canPickTemplate
        ? '说说想做的事，计划与结论会随对话整理；需要固定结构时，也可先选一个模板。'
        : '说说想做的事，计划与结论会随对话整理。'}
    />
    {canPickTemplate && (
      <div className={styles.action}>
        <Popover
          label="选择模板"
          placement="below"
          isOpen={open}
          onOpenChange={(nextOpen) => {
            if (applyingId !== null) return;
            setOpen(nextOpen);
            if (!nextOpen) setError(null);
          }}
          content={
            <div className={styles.panel}>
              <div className={styles.list} role="group" aria-label="可用模板">
                {readyTemplates.map(template => {
                  const busy = applyingId === template.id;
                  return (
                    <button
                      key={template.id}
                      type="button"
                      className={styles.option}
                      disabled={applyingId !== null}
                      onClick={() => { void applyTemplate(template.id); }}
                    >
                      <span className={styles.optionTitle}>{busy ? `正在套用 ${template.title}…` : template.title}</span>
                      {template.description != null && template.description.trim() !== '' && (
                        <span className={styles.optionDesc}>{template.description}</span>
                      )}
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
              variant="secondary"
              label={applyingId !== null ? '正在套用…' : '选择模板'}
              isDisabled={applyingId !== null}
            />
          )}
        </Popover>
      </div>
    )}
  </div>;
}
