// Native publication-grade Unified Track Flow component (`builtin:flow`).
// Renders topologically layered DAG steps, watch probe progress, an interactive editorial detail
// panel, a quiet periodic cron footnote strip, and a collapsible archived history drawer.

import { useMemo, type ReactNode } from 'react';
import type { CheckpointCheck } from '../../../../../core/domain/report-checkpoints.ts';
import type {
  FlowArchivedNodeProjection,
  FlowNodeProjection,
  FlowProjection,
} from '../../../../../core/domain/report-flow.ts';
import type { ReportTaskRow } from '../../../../../core/domain/report.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import { EditorialByline, EditorialGateStrip } from '../editorial/primitives.tsx';
import {
  archivedOutcomeLabel,
  archivedOutcomeTone,
  classifyFlowNodeState,
  formatDoKindLabel,
  formatEdgeCondition,
  formatFlowTimestamp,
  formatNodeCaption,
  formatWatchSummary,
  formatWhenRule,
  isNodeCancellable,
  pickDefaultFlowNodeId,
  toEditorialBylineItem,
  type FlowVisualState,
} from './format.ts';
import { buildFlowLayout, type FlowLayeredNode } from './layout.ts';
import styles from './flow.module.css';

export type ReportFlowBlockProps = Readonly<{
  flow: FlowProjection;
  title?: string;
  taskRows?: readonly ReportTaskRow[];
  renderText?: (text: string) => ReactNode;
  renderMarkdown?: (markdown: string, key: string) => ReactNode;
  onOpenWorker?: (sessionId: string) => void;
  onCancelFlowNode?: (nodeId: string) => Promise<void> | void;
}>;

function formatLayerNumber(index: number): string {
  return String(index + 1).padStart(2, '0');
}

function NodeStateMarker({ state }: Readonly<{ state: FlowVisualState }>) {
  switch (state) {
    case 'ok':
      return (
        <span className={`${styles.stateMarker} ${styles.markerOk}`} aria-hidden="true">
          <Icon name="check" size="sm" />
        </span>
      );
    case 'running':
      return (
        <span className={styles.stateMarker} aria-hidden="true">
          <span className={styles.pulseDots}>
            <span className={styles.pulseDot} />
            <span className={styles.pulseDot} />
            <span className={styles.pulseDot} />
          </span>
        </span>
      );
    case 'waiting':
      return (
        <span className={styles.stateMarker} aria-hidden="true">
          <span className={styles.markerWaiting} />
        </span>
      );
    case 'attention':
      return (
        <span className={`${styles.stateMarker} ${styles.markerAttention}`} aria-hidden="true">
          !
        </span>
      );
    case 'blocked':
    case 'failed':
      return (
        <span className={`${styles.stateMarker} ${styles.markerBlocked}`} aria-hidden="true">
          ×
        </span>
      );
    case 'canceled':
      return (
        <span className={`${styles.stateMarker} ${styles.markerCanceled}`} aria-hidden="true">
          —
        </span>
      );
    case 'pending':
      return (
        <span className={styles.stateMarker} aria-hidden="true">
          <span className={styles.markerPending} />
        </span>
      );
  }
}

function nodeAccentClass(state: FlowVisualState): string {
  switch (state) {
    case 'attention': return styles.nodeAttention;
    case 'blocked':
    case 'failed': return styles.nodeBlocked;
    case 'canceled': return styles.nodeCanceled;
    default: return '';
  }
}

function captionToneClass(state: FlowVisualState): string {
  switch (state) {
    case 'running': return styles.captionRunning;
    case 'waiting': return styles.captionWaiting;
    case 'attention': return styles.captionAttention;
    case 'blocked':
    case 'failed': return styles.captionBlocked;
    default: return '';
  }
}

function archiveToneClass(item: FlowArchivedNodeProjection): string {
  switch (archivedOutcomeTone(item)) {
    case 'ok': return styles.archiveToneOk;
    case 'warn': return styles.archiveToneWarn;
    case 'fail': return styles.archiveToneFail;
    case 'muted': return styles.archiveToneMuted;
  }
}

function toEditorialChecks(node: FlowNodeProjection): readonly CheckpointCheck[] {
  return node.gates.map((gate) => ({
    label: gate.label,
    status: gate.status,
    detail: gate.detail ?? undefined,
    command: gate.command ?? undefined,
    output: gate.output ?? undefined,
  }));
}

function FlowDagNodeCard({
  item,
  isSelected,
  titleById,
  onSelect,
}: Readonly<{
  item: FlowLayeredNode;
  isSelected: boolean;
  titleById: ReadonlyMap<string, string>;
  onSelect: (nodeId: string) => void;
}>) {
  const { node, incoming } = item;
  const visual = classifyFlowNodeState(node);
  const caption = formatNodeCaption(node, titleById);
  const showSummaryPreview = (visual === 'attention' || visual === 'blocked' || visual === 'failed')
    && node.outcomeSummary != null
    && node.outcomeSummary.trim() !== '';

  return (
    <button
      type="button"
      role="tab"
      aria-selected={isSelected}
      className={`${styles.nodeButton} ${isSelected ? styles.nodeSelected : ''} ${nodeAccentClass(visual)}`}
      onClick={() => { onSelect(node.id); }}
    >
      <span className={styles.nodeTopLine}>
        <NodeStateMarker state={visual} />
        <span className={styles.nodeTitle}>{node.title}</span>
      </span>

      {incoming.length > 0 && (
        <span className={styles.nodeEdges}>
          {incoming.map((edge, idx) => {
            const cond = formatEdgeCondition(edge.on);
            const condClass = edge.on === 'attention'
              ? styles.edgeConditionWarn
              : edge.on === 'blocked'
                ? styles.edgeConditionFail
                : '';
            return (
              <span key={`${edge.fromId}-${edge.on}-${idx}`}>
                ← {edge.fromTitle}
                {cond !== null && <span className={condClass}> · {cond}</span>}
              </span>
            );
          })}
        </span>
      )}

      <span className={`${styles.nodeCaption} ${captionToneClass(visual)}`}>{caption}</span>

      {showSummaryPreview && (
        <span className={styles.nodeSummaryPreview}>{node.outcomeSummary}</span>
      )}
    </button>
  );
}

export function ReportFlowBlock({
  flow,
  title,
  taskRows,
  renderText,
  renderMarkdown,
  onOpenWorker,
  onCancelFlowNode,
}: ReportFlowBlockProps) {
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [activeGateKey, setActiveGateKey] = useState<string | null>(null);
  const [archivedOpen, setArchivedOpen] = useState(false);
  const [cancelingId, setCancelingId] = useState<string | null>(null);
  const [cancelError, setCancelError] = useState<string | null>(null);

  const layout = useMemo(() => buildFlowLayout(flow.nodes), [flow.nodes]);
  const defaultNodeId = useMemo(() => pickDefaultFlowNodeId(flow.nodes), [flow.nodes]);
  const titleById = useMemo(
    () => new Map(flow.nodes.map((node) => [node.id, node.title] as const)),
    [flow.nodes],
  );

  const activeNode = useMemo(() => {
    if (selectedId !== null) {
      const picked = flow.nodes.find((node) => node.id === selectedId);
      if (picked !== undefined) return picked;
    }
    if (defaultNodeId !== null) {
      const fallback = flow.nodes.find((node) => node.id === defaultNodeId);
      if (fallback !== undefined) return fallback;
    }
    return flow.nodes[0] ?? null;
  }, [defaultNodeId, flow.nodes, selectedId]);

  const text = (value: string): ReactNode => (renderText !== undefined ? renderText(value) : value);

  const handleSelectNode = (nodeId: string) => {
    setSelectedId(nodeId);
    setActiveGateKey(null);
    setCancelError(null);
  };

  const handleCancelNode = (nodeId: string) => {
    if (onCancelFlowNode === undefined || cancelingId !== null) return;
    setCancelingId(nodeId);
    setCancelError(null);
    void Promise.resolve(onCancelFlowNode(nodeId)).then(
      () => {
        setCancelingId(null);
      },
      (error: unknown) => {
        setCancelingId(null);
        setCancelError(error instanceof Error ? error.message : '取消节点失败，请稍后重试。');
      },
    );
  };

  const headingLabel = title != null && title.trim() !== '' ? title : '调度流程';

  return (
    <section className={styles.root} aria-label={headingLabel}>
      <header className={styles.header}>
        <h2 className={styles.heading}>{headingLabel}</h2>
        {layout.hasCycle && (
          <span className={styles.cycleNotice} role="status">
            检测到依赖环，已按降级层展示
          </span>
        )}
      </header>

      {/* 1. Topologically layered DAG canvas */}
      {layout.layers.length > 0 && (
        <nav className={styles.graphRail} aria-label="调度步骤图">
          <div className={styles.layers} role="tablist" aria-label="流程节点">
            {layout.layers.map((layer) => (
              <div
                key={`layer-${layer.index}-${layer.isCycleFallback ? 'cycle' : 'dag'}`}
                className={styles.layerColumn}
                data-flow-layer={layer.index}
              >
                <div className={styles.layerIndex}>
                  {layer.isCycleFallback ? '环依赖' : formatLayerNumber(layer.index)}
                </div>
                <div className={styles.layerNodes}>
                  {layer.nodes.map((layered) => (
                    <FlowDagNodeCard
                      key={layered.node.id}
                      item={layered}
                      isSelected={activeNode?.id === layered.node.id}
                      titleById={titleById}
                      onSelect={handleSelectNode}
                    />
                  ))}
                </div>
              </div>
            ))}
          </div>
        </nav>
      )}

      {/* 2. Calm editorial detail panel for the selected node */}
      {activeNode !== null && (() => {
        const visual = classifyFlowNodeState(activeNode);
        const bylineItem = toEditorialBylineItem(activeNode);
        const checks = toEditorialChecks(activeNode);
        const canCancel = onCancelFlowNode !== undefined && isNodeCancellable(activeNode);
        const isCanceling = cancelingId === activeNode.id;
        const reusesSession = activeNode.targetSession != null && activeNode.targetSession.startsWith('ses_');
        const hasOutcomeSummary = activeNode.outcomeSummary != null && activeNode.outcomeSummary.trim() !== '';
        const whenRule = formatWhenRule(activeNode, titleById);

        return (
          <article className={styles.detailArticle} role="tabpanel" aria-label={activeNode.title}>
            <header className={styles.detailHeader}>
              <div className={styles.detailTitleRow}>
                <h3 className={styles.detailTitle}>{activeNode.title}</h3>
                {canCancel && (
                  <button
                    type="button"
                    className={styles.cancelButton}
                    disabled={isCanceling}
                    onClick={() => { handleCancelNode(activeNode.id); }}
                  >
                    {isCanceling ? '取消中…' : '取消节点'}
                  </button>
                )}
              </div>

              <EditorialByline
                item={bylineItem}
                taskRows={taskRows}
                onOpenWorker={onOpenWorker}
              />

              <div className={styles.metaLine}>
                <span>{whenRule}</span>
                <span className={styles.metaSep} aria-hidden="true">·</span>
                <span>{formatDoKindLabel(activeNode.doKind)}</span>
                {activeNode.role != null && activeNode.role.trim() !== '' && (
                  <>
                    <span className={styles.metaSep} aria-hidden="true">·</span>
                    <span>{activeNode.role}</span>
                  </>
                )}
                {activeNode.runCount > 0 && (
                  <>
                    <span className={styles.metaSep} aria-hidden="true">·</span>
                    <span>第 {activeNode.runCount} 轮</span>
                  </>
                )}
                {reusesSession && (
                  <>
                    <span className={styles.metaSep} aria-hidden="true">·</span>
                    <span className={styles.contextReuseHint}>复用上下文</span>
                  </>
                )}
              </div>
            </header>

            {cancelError !== null && (
              <div className={styles.cancelError} role="alert">
                {cancelError}
              </div>
            )}

            {hasOutcomeSummary && (
              <div
                className={
                  visual === 'attention'
                    ? styles.attentionBanner
                    : visual === 'blocked' || visual === 'failed'
                      ? styles.blockedBanner
                      : styles.detailProse
                }
                role={visual === 'attention' || visual === 'blocked' || visual === 'failed' ? 'alert' : undefined}
              >
                {renderMarkdown !== undefined
                  ? renderMarkdown(activeNode.outcomeSummary!, `flow-outcome-${activeNode.id}`)
                  : <p>{text(activeNode.outcomeSummary!)}</p>}
              </div>
            )}

            {!hasOutcomeSummary && activeNode.cmd != null && activeNode.cmd.trim() !== '' && (
              <pre className={styles.cmdPreview}>$ {activeNode.cmd}</pre>
            )}

            {!hasOutcomeSummary && activeNode.prompt != null && activeNode.prompt.trim() !== '' && (
              <p className={styles.promptPreview}>{text(activeNode.prompt)}</p>
            )}

            {activeNode.watchProgress != null && (
              <div className={styles.watchPanel} role="region" aria-label="探针等待进度">
                <div className={styles.watchHeadline}>
                  <span>{activeNode.watchProgress.probeKind === 'prChecks' ? 'PR Checks' : 'CMD 探针'}</span>
                  <span>{formatWatchSummary(activeNode.watchProgress)}</span>
                </div>
                <div className={styles.watchMeta}>
                  {formatFlowTimestamp(activeNode.watchProgress.lastCheckedAtMs) !== null && (
                    <span>上次检查 {formatFlowTimestamp(activeNode.watchProgress.lastCheckedAtMs)}</span>
                  )}
                  {formatFlowTimestamp(activeNode.watchProgress.timeoutAtMs) !== null && (
                    <span>超时截止 {formatFlowTimestamp(activeNode.watchProgress.timeoutAtMs)}</span>
                  )}
                </div>
                {activeNode.watchProgress.detail != null && activeNode.watchProgress.detail.trim() !== '' && (
                  <p className={styles.watchDetail}>{text(activeNode.watchProgress.detail)}</p>
                )}
              </div>
            )}

            {checks.length > 0 && (
              <EditorialGateStrip
                itemId={`flow-${activeNode.id}`}
                checks={checks}
                activeGateKey={activeGateKey}
                onSelectGateKey={setActiveGateKey}
                renderText={renderText}
              />
            )}
          </article>
        );
      })()}

      {/* 3. Quiet periodic cron schedule footnote strip */}
      {layout.cronNodes.length > 0 && (
        <section className={styles.cronSection} aria-label="周期巡检规则">
          <h3 className={styles.footnoteHeading}>周期巡检</h3>
          <ul className={styles.cronList}>
            {layout.cronNodes.map((cronNode) => {
              const nextFire = formatFlowTimestamp(cronNode.nextFireAtMs);
              const isSelected = activeNode?.id === cronNode.id;
              return (
                <li key={cronNode.id}>
                  <button
                    type="button"
                    className={`${styles.cronRowButton} ${isSelected ? styles.cronRowSelected : ''}`}
                    onClick={() => { handleSelectNode(cronNode.id); }}
                  >
                    <span className={styles.cronTitle}>{cronNode.title}</span>
                    {cronNode.cron != null && cronNode.cron.trim() !== '' && (
                      <>
                        <span className={styles.metaSep} aria-hidden="true">·</span>
                        <span className={styles.cronExpr}>{cronNode.cron}</span>
                      </>
                    )}
                    <span className={styles.metaSep} aria-hidden="true">·</span>
                    <span>已执行 {cronNode.firedCount} 次</span>
                    {cronNode.canceled || cronNode.status === 'canceled' ? (
                      <>
                        <span className={styles.metaSep} aria-hidden="true">·</span>
                        <span>已取消</span>
                      </>
                    ) : nextFire !== null && (
                      <>
                        <span className={styles.metaSep} aria-hidden="true">·</span>
                        <span>下次触发 {nextFire}</span>
                      </>
                    )}
                  </button>
                </li>
              );
            })}
          </ul>
        </section>
      )}

      {/* 4. Collapsible archived history drawer */}
      {flow.archived.length > 0 && (
        <div className={styles.archiveSection}>
          <button
            type="button"
            className={styles.archiveToggle}
            aria-expanded={archivedOpen}
            onClick={() => { setArchivedOpen((prev) => !prev); }}
          >
            <span aria-hidden="true">{archivedOpen ? '▾' : '▸'}</span>
            <span>已归档 {flow.archived.length} 项历史步骤</span>
          </button>

          {archivedOpen && (
            <div className={styles.archiveWrap}>
              <table className={styles.archiveTable} aria-label="已归档历史步骤">
                <thead>
                  <tr>
                    <th>步骤</th>
                    <th>结论</th>
                    <th>模型</th>
                    <th>归档时间</th>
                  </tr>
                </thead>
                <tbody>
                  {flow.archived.map((item) => {
                    const ts = formatFlowTimestamp(item.finishedAtMs ?? item.archivedAtMs) ?? '—';
                    const modelDisplay = item.model ?? (item.sessionId != null ? 'Worker' : '—');
                    return (
                      <tr key={`${item.id}-${item.archivedAtMs}`}>
                        <td>
                          <div className={styles.archiveTitle}>{item.title}</div>
                          {item.outcomeSummary != null && item.outcomeSummary.trim() !== '' && (
                            <div className={styles.archiveSummary}>{text(item.outcomeSummary)}</div>
                          )}
                        </td>
                        <td className={archiveToneClass(item)}>{archivedOutcomeLabel(item)}</td>
                        <td>
                          {item.sessionId != null && onOpenWorker !== undefined ? (
                            <button
                              type="button"
                              className={styles.archiveModelBtn}
                              onClick={() => { onOpenWorker(item.sessionId!); }}
                            >
                              {modelDisplay}
                            </button>
                          ) : (
                            <span className={styles.archiveMono}>{modelDisplay}</span>
                          )}
                        </td>
                        <td className={styles.archiveMono}>{ts}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </section>
  );
}
