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

const SURFACES = Object.freeze([
  Object.freeze({
    name: 'Report（本页）',
    role: '持续演进的活文档——计划、研究、判断与实时图表都沉淀在这里，而不是埋在对话记录里。',
  }),
  Object.freeze({
    name: 'Conversation',
    role: '工作对话流——用于提出目标、澄清问题、协同推进，以及简要同步本页发生了什么变化。',
  }),
  Object.freeze({
    name: 'Template & Flow',
    role: '可选的结构与编排——为投研、阶段交付或周期闭环提供专属视图、数据与日程，随时可调可撤。',
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

  return <div className={styles.sheet} data-nc-report-empty="">
    <div className={styles.protocolBar} aria-label="默认引导状态">
      <div className={styles.protocolBadges}>
        <span className={styles.protocolName}>Perga 空白工作区</span>
        <span className={styles.protocolPill}>缺省引导</span>
      </div>
      <span className={styles.protocolHint}>落笔或套用模板后自动替换</span>
    </div>

    <div className={styles.doc}>
      <section className={styles.section} aria-label="关于本页">
        <h2 className={styles.chapter}>
          <span className={styles.chapterNum} aria-hidden="true">01</span>
          <span>进展会整理在这里</span>
        </h2>
        <p className={styles.prose}>
          <strong>Perga</strong> 以一份持续演进的 <strong>Report</strong> 为中心组织每一项工作。无论是长期投研、工程交付还是周期复盘，关键结论、数据与过程都会直接呈现在这张纸面上。
        </p>
        <p className={styles.prose}>
          你现在可以直接在右侧与 <strong>Lead</strong> 开始对话——明确目标后，它会自动为这项工作拟定标题，在需要时主动匹配合适模板，并将成果持续整理进本页。
        </p>
      </section>

      <section className={styles.section} aria-label="工作方式">
        <h3 className={styles.chapter}>
          <span className={styles.chapterNum} aria-hidden="true">02</span>
          <span>工作方式</span>
        </h3>
        <dl className={styles.surfaceTable}>
          {SURFACES.map(item => (
            <div key={item.name} className={styles.surfaceRow}>
              <dt className={styles.surfaceName}>{item.name}</dt>
              <dd className={styles.surfaceRole}>{item.role}</dd>
            </div>
          ))}
        </dl>
      </section>

      <section className={styles.section} aria-label="从模板起稿">
        <div className={styles.chapterRow}>
          <h3 className={styles.chapter}>
            <span className={styles.chapterNum} aria-hidden="true">03</span>
            <span>从模板起稿</span>
          </h3>
          <span className={styles.chapterMeta}>{formatIndex(readyTemplates.length - 1)} 份可用</span>
        </div>
        <p className={styles.prose}>
          如果这项工作适合固定结构，点选下面任一模板即可直接铺好初始报告与视图；也可以保持空白，在新建或对话时随时通过 <code className={styles.codeCue}>@:</code> 引用，或交给 Lead 自行判断。
        </p>

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
      </section>
    </div>
  </div>;
}
