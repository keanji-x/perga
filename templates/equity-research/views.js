/** @type {Record<string, string>} */
const VERDICT_FALLBACK = {
  pending: "待兑现",
  beat: "超预期",
  inline: "符合预期",
  miss: "低于预期",
};

export const views = {
  /** @param {Record<string, any>} data */
  valuationAxis(data) {
    const v = data.valuation || { unit: "$", currentPrice: 0, targets: [] };
    /** @type {any[]} */
    const targets = Array.isArray(v.targets) ? v.targets : [];
    return {
      ticker: data.ticker,
      unit: v.unit || "$",
      current: {
        value: v.currentPrice,
        display: `${v.unit || "$"}${Number(v.currentPrice).toFixed(2)}`,
        label: v.currentLabel || "现价",
      },
      points: targets.map((pt) => ({
        value: pt.price,
        display: `${v.unit || "$"}${pt.price}`,
        emphasis: Boolean(pt.emphasis),
        note: pt.assumption,
        docRef: pt.docRef,
      })),
    };
  },

  /**
   * @param {Record<string, any>} data
   * @param {import("../perga-template.d.ts").KernelFactsSnapshot} kernel
   */
  eventChronicle(data, kernel) {
    const tasks = kernel && kernel.tasks ? kernel.tasks : {};
    /** @type {any[]} */
    const events = Array.isArray(data.events) ? data.events.slice() : [];
    events.sort((a, b) => String(b.date || "").localeCompare(String(a.date || "")));

    return {
      items: events.map((e) => {
        const kTask = e.taskRef ? tasks[e.taskRef] : undefined;
        const effectiveStatus = kTask ? kTask.status : (e.status || (e.verdict === "pending" ? "pending" : "done"));
        return {
          id: e.id,
          date: e.date,
          title: e.title,
          status: effectiveStatus,
          verdict: e.verdictLabel || VERDICT_FALLBACK[e.verdict] || e.verdict,
          taskKey: e.taskRef,
          sessionId: kTask ? kTask.sessionId : undefined,
          model: kTask ? kTask.model : e.assign,
          assign: kTask && kTask.model ? kTask.model : e.assign,
          assignKind: e.assignKind === "cli" ? "command" : "model",
          triggerAt: e.triggerAt,
          summary: e.summary,
          expectations: e.expects,
        };
      }),
    };
  },
};
