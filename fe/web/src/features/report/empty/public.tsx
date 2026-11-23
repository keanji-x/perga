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

const INLINE_TEMPLATE_LIMIT = 4;

const PARCHMENT_NOTES = Object.freeze([
  Object.freeze({
    index: '01',
    title: '对话即起稿',
    body: '说明想推进的事，Lead 会自动拟定标题、整理要点并持续修订本页。',
  }),
  Object.freeze({
    index: '02',
    title: '按需定结构',
    body: '面对投研、交付或周期闭环，Lead 会在动笔前主动匹配合适模板。',
  }),
  Object.freeze({
    index: '03',
    title: '活文档演进',
    body: '图表视图与正文结论分层维护，模板与日程随时可调可撤。',
  }),
]);

function formatIndex(index: number): string {
  return String(index + 1).padStart(2, '0');
}

export function ReportEmpty({ templates, onApplyTemplate }: ReportEmptyProps = {}) {
  const [moreOpen, setMoreOpen] = useState(false);
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
      setMoreOpen(false);
    } catch (failure: unknown) {
      setError(readErrorText(failure, '套用模板失败，请稍后重试。'));
    } finally {
      setApplyingId(null);
    }
  };

  if (!canPickTemplate) {
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

  return <div className={`${styles.empty} ${styles.folioWrap}`} data-nc-report-empty="">
    <article className={styles.folio} aria-label="空白报告引导">
      <header className={styles.folioHead}>
        <div className={styles.eyebrowRow}>
          <span className={styles.eyebrow}>
            <span className={styles.eyebrowIcon} aria-hidden="true"><Icon name="file" size="sm" /></span>
            <span>卷首 · 空白报告</span>
          </span>
          <span className={styles.folioCode} aria-hidden="true">FOLIO 00</span>
        </div>
        <div className={styles.doubleRule} aria-hidden="true" />
        <h2 className={styles.folioTitle}>进展会整理在这里</h2>
        <p className={styles.folioLead}>
          这里是这项工作的活文档——计划、研究、判断与实时视图都会随推进沉淀于此，而不是散落在零碎的对话记录里。你可以直接与 Lead 开启对话，也可以先为这一页铺好结构。
        </p>
      </header>

      <ol className={styles.notes} aria-label="使用说明">
        {PARCHMENT_NOTES.map(note => (
          <li key={note.index} className={styles.note}>
            <span className={styles.noteIndex} aria-hidden="true">{note.index}</span>
            <div className={styles.noteBody}>
              <p className={styles.noteTitle}>{note.title}</p>
              <p className={styles.noteDesc}>{note.body}</p>
            </div>
          </li>
        ))}
      </ol>

      <section className={styles.ledger} aria-label="从模板起稿">
        <div className={styles.ledgerHead}>
          <h3 className={styles.ledgerTitle}>从模板起稿</h3>
          <span className={styles.ledgerMeta}>{formatIndex(readyTemplates.length - 1)} 份可用 · 点击即刻铺页</span>
        </div>

        <div className={styles.ledgerList} role="group" aria-label="可用模板">
          {featuredTemplates.map((template, index) => {
            const busy = applyingId === template.id;
            return (
              <button
                key={template.id}
                type="button"
                className={styles.ledgerRow}
                disabled={applyingId !== null}
                onClick={() => { void applyTemplate(template.id); }}
              >
                <span className={styles.rowIndex} aria-hidden="true">{formatIndex(index)}</span>
                <span className={styles.rowBody}>
                  <span className={styles.rowTitle}>{busy ? `正在铺页 · ${template.title}…` : template.title}</span>
                  {template.description != null && template.description.trim() !== '' && (
                    <span className={styles.rowDesc}>{template.description}</span>
                  )}
                </span>
                <span className={styles.rowAction} aria-hidden="true">
                  <span>{busy ? '套用中' : '套用'}</span>
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
                  {overflowTemplates.map((template, index) => {
                    const busy = applyingId === template.id;
                    return (
                      <button
                        key={template.id}
                        type="button"
                        className={styles.ledgerRow}
                        disabled={applyingId !== null}
                        onClick={() => { void applyTemplate(template.id); }}
                      >
                        <span className={styles.rowIndex} aria-hidden="true">{formatIndex(INLINE_TEMPLATE_LIMIT + index)}</span>
                        <span className={styles.rowBody}>
                          <span className={styles.rowTitle}>{busy ? `正在铺页 · ${template.title}…` : template.title}</span>
                          {template.description != null && template.description.trim() !== '' && (
                            <span className={styles.rowDesc}>{template.description}</span>
                          )}
                        </span>
                        <span className={styles.rowAction} aria-hidden="true">
                          <span>{busy ? '套用中' : '套用'}</span>
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
      </section>

      <footer className={styles.colophon}>
        <span className={styles.colophonLine}>
          亦可直接开始对话，或在新建时输入 <code className={styles.codeCue}>@:</code> 快速引用模板；文档落笔后此扉页自动隐去。
        </span>
      </footer>
    </article>
  </div>;
}
