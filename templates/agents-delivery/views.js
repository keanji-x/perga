/**
 * @param {Record<string, any> | undefined} node
 * @param {Record<string, any>} tasks
 */
function hydrateNode(node, tasks) {
  const safe = node && typeof node === "object" ? node : {};
  const kTask = safe.taskRef ? tasks[safe.taskRef] : undefined;
  const gates = kTask && Array.isArray(kTask.gates) && kTask.gates.length > 0
    ? kTask.gates
    : (Array.isArray(safe.checks) ? safe.checks : []);

  const summaryParts = [];
  if (safe.summary && typeof safe.summary === "string" && safe.summary.trim() !== "") {
    summaryParts.push(safe.summary.trim());
  }
  if (safe.docRef && typeof safe.docRef === "string" && safe.docRef.trim() !== "") {
    const label = safe.docLabel || safe.docRef;
    summaryParts.push(`参考文档：[${label}](${safe.docRef})`);
  }

  return {
    id: safe.id || undefined,
    title: safe.title || "",
    scope: safe.scope || undefined,
    status: kTask ? kTask.status : (safe.status || "pending"),
    verdict: safe.verdict || undefined,
    statusLabel: safe.verdict === "changes_requested"
      ? "打回修复"
      : (safe.verdict === "pass" ? "已通过" : undefined),
    round: typeof safe.round === "number" ? safe.round : undefined,
    taskKey: safe.taskRef || undefined,
    sessionId: kTask ? kTask.sessionId : undefined,
    model: kTask && kTask.model ? kTask.model : safe.assign,
    assign: kTask && kTask.model ? kTask.model : safe.assign,
    assignKind: safe.assignKind === "cli" ? "command" : "model",
    summary: summaryParts.join("\n\n"),
    blockers: Array.isArray(safe.blockers) ? safe.blockers : [],
    docRef: safe.docRef || undefined,
    docLabel: safe.docLabel || undefined,
    triggerAt: safe.triggerAt || undefined,
    checks: gates,
  };
}

/**
 * @param {any[]} triggers
 */
export function createViews(triggers = []) {
  const standingTriggers = (Array.isArray(triggers) ? triggers : []).map((tr) => ({
    trigger: tr.on && tr.on.summary ? tr.on.summary : (tr.on && tr.on.expr ? tr.on.expr : (tr.on && tr.on.kind) || "条件触发"),
    action: tr.label || tr.id,
    assign: tr.action && (tr.action.command || tr.action.model || tr.action.role) || "—",
    assignKind: tr.action && tr.action.kind === "run_cli" ? "command" : "model",
  }));

  return {
    /**
     * Projects BOTH the pure SVG topology graph (`./widgets/delivery-flow.html`)
     * AND the native Perga Markdown editorial detail panel (`builtin:detail`)
     * linked via `selectionKey: "node"`.
     * @param {Record<string, any>} data
     * @param {import("../perga-template.d.ts").KernelFactsSnapshot} kernel
     */
    deliveryFlow(data, kernel) {
      const tasks = kernel && kernel.tasks ? kernel.tasks : {};
      /** @type {any[]} */
      const rawReviews = Array.isArray(data.reviews) ? data.reviews : [];
      const reviewTier = data.reviewTier || "L2";
      /** @type {any[]} */
      const rounds = (Array.isArray(data.rounds) ? data.rounds : []).map((rd) => ({
        ...rd,
        outcomeLabel: rd.outcome === "pass"
          ? "复审通过"
          : (rd.outcome === "changes_requested" ? "打回修复" : "进行中"),
      }));

      const design = { ...hydrateNode(data.design, tasks), id: "design", code: "01" };
      const implement = { ...hydrateNode(data.implement, tasks), id: "implement", code: "02", rounds };
      const reviews = rawReviews.map((r, idx) => ({
        ...hydrateNode(r, tasks),
        id: r.id || `review-${idx + 1}`,
        code: `03${String.fromCharCode(97 + idx)}`,
        rounds,
      }));
      const mergeBase = hydrateNode(data.merge, tasks);
      const merge = {
        ...mergeBase,
        id: "merge",
        code: "04",
        triggers: mergeBase.status === "pending" ? standingTriggers : [],
      };

      const allNodes = [design, implement, ...reviews, merge];
      const defaultSelected = allNodes.find((n) => n.status === "blocked" || n.verdict === "changes_requested")
        || allNodes.find((n) => n.status === "running")
        || reviews[0]
        || implement;

      return {
        reviewTier,
        blockersTitle: "发现的阻断项 / 修复清单",
        roundsTitle: `修复闭环记录 · ${reviewTier}`,
        currentRound: typeof data.currentRound === "number" ? data.currentRound : 1,
        defaultSelectedId: defaultSelected ? defaultSelected.id : "design",
        design,
        implement,
        reviews,
        merge,
        items: allNodes,
        rounds,
        triggers: standingTriggers,
      };
    },
  };
}
