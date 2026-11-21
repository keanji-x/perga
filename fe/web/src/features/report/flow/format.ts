// Pure formatting, state classification, and priority selection helpers for `builtin:flow`.

import type { CheckpointItem, CheckpointStatus } from '../../../../../core/domain/report-checkpoints.ts';
import type {
  FlowArchivedNodeProjection,
  FlowNodeProjection,
  FlowOutcomeCondition,
  FlowWatchProgress,
} from '../../../../../core/domain/report-flow.ts';

export type FlowVisualState =
  | 'ok'
  | 'running'
  | 'waiting'
  | 'attention'
  | 'blocked'
  | 'failed'
  | 'pending'
  | 'canceled';

export function classifyFlowNodeState(node: FlowNodeProjection): FlowVisualState {
  if (node.canceled || node.status === 'canceled') return 'canceled';
  if (node.outcome === 'attention') return 'attention';
  if (node.outcome === 'blocked') return 'blocked';
  if (node.status === 'failed') return 'failed';
  if (node.status === 'waiting') return 'waiting';
  if (node.status === 'running' || node.status === 'verifying') {
    return node.doKind === 'watch' && node.watchProgress != null ? 'waiting' : 'running';
  }
  if (node.status === 'done') return 'ok';
  return 'pending';
}

export function flowStateLabel(node: FlowNodeProjection): string {
  const visual = classifyFlowNodeState(node);
  switch (visual) {
    case 'ok': return '已完成';
    case 'running': return node.status === 'verifying' ? '验证中' : '运行中';
    case 'waiting': return '等待中';
    case 'attention': return '需介入';
    case 'blocked': return '阻塞';
    case 'failed': return '失败';
    case 'canceled': return '已取消';
    case 'pending': return '待调度';
  }
}

export function archivedOutcomeLabel(item: FlowArchivedNodeProjection): string {
  if (item.status === 'canceled') return '已取消';
  if (item.outcome === 'attention') return '需介入';
  if (item.outcome === 'blocked') return '阻塞';
  if (item.status === 'failed') return '失败';
  return '已完成';
}

export function archivedOutcomeTone(item: FlowArchivedNodeProjection): 'ok' | 'warn' | 'fail' | 'muted' {
  if (item.status === 'canceled') return 'muted';
  if (item.outcome === 'attention') return 'warn';
  if (item.outcome === 'blocked' || item.status === 'failed') return 'fail';
  return 'ok';
}

export function isNodeCancellable(node: FlowNodeProjection): boolean {
  if (node.canceled || node.status === 'canceled') return false;
  if (node.whenKind === 'cron') return true;
  return node.status === 'pending'
    || node.status === 'waiting'
    || node.status === 'running'
    || node.status === 'verifying';
}

/**
 * Default-selects the highest-priority active node:
 * 1. `attention` / `blocked` / `failed` (non-canceled)
 * 2. `running` / `verifying` / `waiting`
 * 3. first `pending`
 * 4. latest `done` (highest `finishedAtMs`, tie-broken by latest index)
 * 5. first node fallback
 */
export function pickDefaultFlowNodeId(nodes: readonly FlowNodeProjection[]): string | null {
  if (nodes.length === 0) return null;

  const urgent = nodes.find((node) => {
    const state = classifyFlowNodeState(node);
    return state === 'attention' || state === 'blocked' || state === 'failed';
  });
  if (urgent !== undefined) return urgent.id;

  const active = nodes.find((node) => {
    const state = classifyFlowNodeState(node);
    return state === 'running' || state === 'waiting';
  });
  if (active !== undefined) return active.id;

  const pending = nodes.find((node) => classifyFlowNodeState(node) === 'pending');
  if (pending !== undefined) return pending.id;

  let latestDone: FlowNodeProjection | null = null;
  for (const node of nodes) {
    if (node.status !== 'done') continue;
    if (latestDone === null) {
      latestDone = node;
      continue;
    }
    const prevTs = latestDone.finishedAtMs ?? 0;
    const nextTs = node.finishedAtMs ?? 0;
    if (nextTs >= prevTs) {
      latestDone = node;
    }
  }
  if (latestDone !== null) return latestDone.id;

  return nodes[0].id;
}

export function formatFlowTimestamp(ms: number | null | undefined): string | null {
  if (ms == null || !Number.isFinite(ms) || ms <= 0) return null;
  const date = new Date(ms);
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  const hours = String(date.getHours()).padStart(2, '0');
  const minutes = String(date.getMinutes()).padStart(2, '0');
  return `${month}-${day} ${hours}:${minutes}`;
}

export function formatWatchSummary(watch: FlowWatchProgress): string {
  const baseLabel = watch.label.trim() !== ''
    ? watch.label.trim()
    : (watch.probeKind === 'prChecks' ? 'PR checks' : 'cmd probe');
  const parts: string[] = [baseLabel];
  if (watch.passedChecks != null && watch.totalChecks != null) {
    const ratio = `${watch.passedChecks}/${watch.totalChecks}`;
    if (!baseLabel.includes(ratio)) {
      parts.push(`${ratio} checks`);
    }
  }
  parts.push('等待中');
  return parts.join(' · ');
}

export function formatEdgeCondition(on: FlowOutcomeCondition): string | null {
  switch (on) {
    case 'ok': return null;
    case 'attention': return '需介入';
    case 'blocked': return '阻塞';
    case 'any': return '任意结果';
  }
}

export function formatWhenRule(
  node: FlowNodeProjection,
  titleById: ReadonlyMap<string, string>,
): string {
  switch (node.whenKind) {
    case 'immediate':
      return '立即触发';
    case 'cron':
      return node.cron != null && node.cron.trim() !== ''
        ? `周期 ${node.cron}${node.tz != null && node.tz.trim() !== '' ? ` (${node.tz})` : ''}`
        : '周期触发';
    case 'at':
      return node.at != null && node.at.trim() !== '' ? `定时 ${node.at}` : '定时触发';
    case 'after': {
      if (node.after.length === 0) return '前置触发';
      const labels = node.after.map((dep) => {
        const name = titleById.get(dep.node) ?? dep.node;
        const cond = formatEdgeCondition(dep.on);
        return cond === null ? name : `${name} (${cond})`;
      });
      return `前置：${labels.join('、')}`;
    }
  }
}

export function formatNodeCaption(
  node: FlowNodeProjection,
  titleById: ReadonlyMap<string, string>,
): string {
  const visual = classifyFlowNodeState(node);
  if (visual === 'waiting') {
    if (node.watchProgress != null) {
      return formatWatchSummary(node.watchProgress);
    }
    if (node.waitingOn.length > 0) {
      const names = node.waitingOn.map((id) => titleById.get(id) ?? id);
      return `等待 ${names.join('、')}`;
    }
    return '等待中';
  }
  if (visual === 'pending') {
    if (node.waitingOn.length > 0) {
      if (node.after.length > 0) {
        return '待前置完成';
      }
      const names = node.waitingOn.map((id) => titleById.get(id) ?? id);
      return `等待 ${names.join('、')}`;
    }
    if (node.whenKind === 'at' && node.at != null && node.at.trim() !== '') {
      return `定时 ${node.at}`;
    }
    return '待调度';
  }
  return flowStateLabel(node);
}

export function formatDoKindLabel(doKind: FlowNodeProjection['doKind']): string {
  switch (doKind) {
    case 'run': return '执行';
    case 'wake': return '唤醒';
    case 'watch': return '监听探针';
  }
}

export function toEditorialBylineItem(node: FlowNodeProjection): CheckpointItem {
  const visual = classifyFlowNodeState(node);
  let status: CheckpointStatus;
  switch (visual) {
    case 'ok':
      status = 'done';
      break;
    case 'running':
    case 'waiting':
      status = 'running';
      break;
    case 'attention':
    case 'blocked':
    case 'failed':
      status = 'blocked';
      break;
    case 'pending':
    case 'canceled':
      status = 'pending';
      break;
  }

  const resolvedSession = node.sessionId
    ?? (node.targetSession != null && node.targetSession.startsWith('ses_') ? node.targetSession : undefined);
  const ts = formatFlowTimestamp(node.finishedAtMs ?? node.startedAtMs ?? node.nextFireAtMs);

  return {
    id: node.id,
    title: node.title,
    status,
    statusLabel: flowStateLabel(node),
    model: node.model ?? undefined,
    assign: node.model == null
      ? (node.cmd != null && node.cmd.trim() !== ''
        ? node.cmd.trim()
        : (node.role ?? (node.targetSession === 'lead' ? 'Lead' : undefined)))
      : undefined,
    assignKind: node.model != null
      ? 'model'
      : (node.cmd != null && node.cmd.trim() !== '' ? 'command' : undefined),
    sessionId: resolvedSession ?? undefined,
    triggerAt: ts ?? undefined,
  };
}
