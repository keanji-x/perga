// Pure topological layering & partitioning helper for Unified Track Flow (`builtin:flow`).
// Partitions `flow.nodes` into:
//   1. Topologically layered DAG columns (all non-cron nodes + any cron node with downstream `after` dependents)
//   2. Periodic `cron` rules for the quiet bottom schedule footnote strip
// Handles cycles defensively via a fail-soft fallback layer so malformed data never crashes the UI.

import type {
  FlowNodeProjection,
  FlowOutcomeCondition,
} from '../../../../../core/domain/report-flow.ts';

export type FlowIncomingEdge = Readonly<{
  fromId: string;
  fromTitle: string;
  on: FlowOutcomeCondition;
}>;

export type FlowLayeredNode = Readonly<{
  node: FlowNodeProjection;
  layer: number;
  incoming: readonly FlowIncomingEdge[];
  outgoingCount: number;
}>;

export type FlowLayer = Readonly<{
  index: number;
  nodes: readonly FlowLayeredNode[];
  isCycleFallback: boolean;
}>;

export type FlowLayout = Readonly<{
  layers: readonly FlowLayer[];
  cronNodes: readonly FlowNodeProjection[];
  hasCycle: boolean;
}>;

export function buildFlowLayout(nodes: readonly FlowNodeProjection[]): FlowLayout {
  if (nodes.length === 0) {
    return { layers: [], cronNodes: [], hasCycle: false };
  }

  const cronNodes: FlowNodeProjection[] = [];
  const referencedAsDependency = new Set<string>();

  for (const node of nodes) {
    if (node.whenKind === 'cron') {
      cronNodes.push(node);
    }
    for (const dep of node.after) {
      if (dep.node !== node.id) {
        referencedAsDependency.add(dep.node);
      }
    }
  }

  // Include all non-cron nodes in the DAG, plus any cron node that triggers downstream `after` dependents.
  const graphNodes = nodes.filter(
    (node) => node.whenKind !== 'cron' || referencedAsDependency.has(node.id),
  );

  if (graphNodes.length === 0) {
    return { layers: [], cronNodes, hasCycle: false };
  }

  const byId = new Map<string, FlowNodeProjection>();
  const orderIndex = new Map<string, number>();
  for (const [idx, node] of graphNodes.entries()) {
    if (!byId.has(node.id)) {
      byId.set(node.id, node);
      orderIndex.set(node.id, idx);
    }
  }

  const uniqueGraphNodes = [...byId.values()];
  const inDegree = new Map<string, number>();
  const dependents = new Map<string, string[]>();
  const incomingMap = new Map<string, FlowIncomingEdge[]>();

  for (const node of uniqueGraphNodes) {
    inDegree.set(node.id, 0);
    dependents.set(node.id, []);
    incomingMap.set(node.id, []);
  }

  for (const node of uniqueGraphNodes) {
    const seenDeps = new Set<string>();
    for (const dep of node.after) {
      if (dep.node === node.id) continue;
      const upstream = byId.get(dep.node);
      if (upstream === undefined) continue;
      incomingMap.get(node.id)?.push({
        fromId: upstream.id,
        fromTitle: upstream.title,
        on: dep.on,
      });
      if (seenDeps.has(dep.node)) continue;
      seenDeps.add(dep.node);
      inDegree.set(node.id, (inDegree.get(node.id) ?? 0) + 1);
      dependents.get(dep.node)?.push(node.id);
    }
  }

  const layerOf = new Map<string, number>();
  const queue: string[] = [];

  for (const node of uniqueGraphNodes) {
    if ((inDegree.get(node.id) ?? 0) === 0) {
      layerOf.set(node.id, 0);
      queue.push(node.id);
    }
  }

  let head = 0;
  let maxLayer = -1;
  while (head < queue.length) {
    const currentId = queue[head];
    head += 1;
    const currentLayer = layerOf.get(currentId) ?? 0;
    if (currentLayer > maxLayer) maxLayer = currentLayer;

    const children = dependents.get(currentId) ?? [];
    for (const childId of children) {
      const nextLayer = Math.max(layerOf.get(childId) ?? 0, currentLayer + 1);
      layerOf.set(childId, nextLayer);
      const remaining = (inDegree.get(childId) ?? 1) - 1;
      inDegree.set(childId, remaining);
      if (remaining === 0) {
        queue.push(childId);
      }
    }
  }

  const buckets = new Map<number, FlowLayeredNode[]>();
  for (const node of uniqueGraphNodes) {
    const layer = layerOf.get(node.id);
    if (layer === undefined) continue;
    const list = buckets.get(layer) ?? [];
    list.push({
      node,
      layer,
      incoming: incomingMap.get(node.id) ?? [],
      outgoingCount: dependents.get(node.id)?.length ?? 0,
    });
    buckets.set(layer, list);
  }

  const layers: FlowLayer[] = [];
  const sortedLayerIndices = [...buckets.keys()].sort((a, b) => a - b);
  for (const idx of sortedLayerIndices) {
    const bucket = buckets.get(idx) ?? [];
    bucket.sort((a, b) => (orderIndex.get(a.node.id) ?? 0) - (orderIndex.get(b.node.id) ?? 0));
    layers.push({ index: idx, nodes: bucket, isCycleFallback: false });
  }

  // Defensive cycle fallback: collect any nodes not visited by Kahn's algorithm.
  const cycleNodes = uniqueGraphNodes.filter((node) => !layerOf.has(node.id));
  const hasCycle = cycleNodes.length > 0;
  if (hasCycle) {
    const fallbackIndex = maxLayer + 1;
    const fallbackItems: FlowLayeredNode[] = cycleNodes.map((node) => ({
      node,
      layer: fallbackIndex,
      incoming: incomingMap.get(node.id) ?? [],
      outgoingCount: dependents.get(node.id)?.length ?? 0,
    }));
    layers.push({ index: fallbackIndex, nodes: fallbackItems, isCycleFallback: true });
  }

  return { layers, cronNodes, hasCycle };
}
