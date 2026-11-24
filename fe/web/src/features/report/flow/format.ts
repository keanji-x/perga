// Pure formatting, state classification, and priority selection helpers for `builtin:flow`.

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
  if (node.status === 'done') {
    return node.doKind === 'watch' && node.repeat && node.outcome === 'ok' ? 'waiting' : 'ok';
  }
  return 'pending';
}

export function flowStateLabel(node: FlowNodeProjection): string {
  const visual = classifyFlowNodeState(node);
  switch (visual) {
    case 'ok': return 'Completed';
    case 'running': return node.status === 'verifying' ? 'Verifying' : 'Running';
    case 'waiting': return node.doKind === 'watch' && node.repeat && node.status === 'done' ? 'Next cycle' : 'Waiting';
    case 'attention': return 'Attention';
    case 'blocked': return 'Blocked';
    case 'failed': return 'Failed';
    case 'canceled': return 'Canceled';
    case 'pending': return 'Scheduled';
  }
}

export function archivedOutcomeLabel(item: FlowArchivedNodeProjection): string {
  if (item.status === 'canceled') return 'Canceled';
  if (item.outcome === 'attention') return 'Attention';
  if (item.outcome === 'blocked') return 'Blocked';
  if (item.status === 'failed') return 'Failed';
  return 'Completed';
}

export function archivedOutcomeTone(item: FlowArchivedNodeProjection): 'ok' | 'warn' | 'fail' | 'muted' {
  if (item.status === 'canceled') return 'muted';
  if (item.outcome === 'attention') return 'warn';
  if (item.outcome === 'blocked' || item.status === 'failed') return 'fail';
  return 'ok';
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

export function formatWatchSummary(watch: FlowWatchProgress, isLive = true): string {
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
  if (isLive) {
    parts.push('Waiting');
  }
  return parts.join(' · ');
}

export function formatEdgeCondition(on: FlowOutcomeCondition): string | null {
  switch (on) {
    case 'ok': return null;
    case 'attention': return 'attention';
    case 'blocked': return 'blocked';
    case 'any': return 'any';
  }
}

export function formatWhenRule(
  node: FlowNodeProjection,
  titleById: ReadonlyMap<string, string>,
): string {
  switch (node.whenKind) {
    case 'immediate':
      return 'Immediate';
    case 'cron':
      return node.cron != null && node.cron.trim() !== ''
        ? `Cron ${node.cron}${node.tz != null && node.tz.trim() !== '' ? ` (${node.tz})` : ''}`
        : 'Recurring';
    case 'at':
      return node.at != null && node.at.trim() !== '' ? `At ${node.at}` : 'Scheduled';
    case 'after': {
      if (node.after.length === 0) return 'After dependencies';
      const labels = node.after.map((dep) => {
        const name = titleById.get(dep.node) ?? dep.node;
        const cond = formatEdgeCondition(dep.on);
        return cond === null ? name : `${name} (${cond})`;
      });
      return `After: ${labels.join(', ')}`;
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
      return `Waiting on ${names.join(', ')}`;
    }
    return 'Waiting';
  }
  if (visual === 'pending') {
    if (node.waitingOn.length > 0) {
      if (node.after.length > 0) {
        return 'Waiting on dependencies';
      }
      const names = node.waitingOn.map((id) => titleById.get(id) ?? id);
      return `Waiting on ${names.join(', ')}`;
    }
    if (node.whenKind === 'at' && node.at != null && node.at.trim() !== '') {
      return `At ${node.at}`;
    }
    return 'Scheduled';
  }
  return flowStateLabel(node);
}

export function formatDoKindLabel(doKind: FlowNodeProjection['doKind']): string {
  switch (doKind) {
    case 'run': return 'Run';
    case 'wake': return 'Wake';
    case 'watch': return 'Watch';
    case 'track': return 'Spawn track';
  }
}
