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
    callouts: { title: "发现的阻断项 / 修复清单", items: Array.isArray(safe.blockers) ? safe.blockers : [] },
    docRef: safe.docRef || undefined,
    docLabel: safe.docLabel || undefined,
    triggerAt: safe.triggerAt || undefined,
    checks: gates,
  };
}

export function createViews() {
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

      const entries = { title: `修复闭环记录 · ${reviewTier}`, items: rounds.map((rd) => ({
        tag: `R${rd.round}`, status: rd.outcome === "pass" ? "pass" : rd.outcome === "changes_requested" ? "fail" : "running",
        statusLabel: rd.outcomeLabel, actor: rd.triggeredBy, summary: rd.summary,
      })) };
      const design = { ...hydrateNode(data.design, tasks), id: "design", code: "01" };
      const implement = { ...hydrateNode(data.implement, tasks), id: "implement", code: "02", entries };
      const reviews = rawReviews.map((r, idx) => ({
        ...hydrateNode(r, tasks),
        id: r.id || `review-${idx + 1}`,
        code: `03${String.fromCharCode(97 + idx)}`,
        entries,
      }));
      const mergeBase = hydrateNode(data.merge, tasks);
      const merge = {
        ...mergeBase,
        id: "merge",
        code: "04",
      };

      const allNodes = [design, implement, ...reviews, merge];
      const defaultSelected = allNodes.find((n) => n.status === "blocked" || n.verdict === "changes_requested")
        || allNodes.find((n) => n.status === "running")
        || reviews[0]
        || implement;

      return {
        reviewTier,
        currentRound: typeof data.currentRound === "number" ? data.currentRound : 1,
        defaultSelectedId: defaultSelected ? defaultSelected.id : "design",
        design,
        implement,
        reviews,
        merge,
        items: allNodes,
        rounds,
      };
    },
  };
}
