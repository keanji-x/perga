/** @type {Record<string, string>} */
const VERDICT_FALLBACK = {
  pending: "待兑现",
  beat: "超预期",
  inline: "符合预期",
  miss: "低于预期",
};

/** @type {Record<string, import("../perga-template.d.ts").EditorialTone>} */
const VERDICT_TONE = {
  pending: "neutral",
  beat: "good",
  inline: "accent",
  miss: "bad",
};

export const views = {
  /**
   * @param {Record<string, any>} data
   * @param {import("../perga-template.d.ts").KernelFactsSnapshot} _kernel
   * @param {Readonly<Record<string, any>>} inputs
   */
  valuationAxis(data, _kernel, inputs) {
    const v = data.valuation || { unit: "$", targets: [] };
    /** @type {any[]} */
    const targets = Array.isArray(v.targets) ? v.targets : [];
    return {
      ticker: inputs?.ticker || data.ticker,
      unit: v.unit || "$",
      current: typeof v.currentPrice === "number" ? {
        value: v.currentPrice,
        display: `${v.unit || "$"}${Number(v.currentPrice).toFixed(2)}`,
        label: v.currentLabel || "现价",
      } : null,
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
   * @returns {import("../perga-template.d.ts").BuiltinListView}
   */
  eventChronicle(data, kernel) {
    const tasks = kernel?.tasks ?? {};
    const events = Array.isArray(data.events) ? data.events.slice() : [];
    events.sort((a, b) => String(b.date || "").localeCompare(String(a.date || "")));

    return {
      items: events.map((e) => {
        const kTask = e.taskRef ? tasks[e.taskRef] : undefined;
        const effectiveStatus = kTask ? kTask.status : (e.status || (e.verdict === "pending" ? "pending" : "done"));
        /** @type {Array<{ label: string, expected: string, actual?: string, status: string }>} */
        const expects = Array.isArray(e.expects) ? e.expects : [];
        return {
          id: e.id,
          date: e.date,
          title: e.title,
          status: effectiveStatus,
          badge: e.verdictLabel || VERDICT_FALLBACK[e.verdict] || e.verdict,
          tone: VERDICT_TONE[e.verdict],
          taskKey: e.taskRef,
          sessionId: kTask ? kTask.sessionId : undefined,
          meta: kTask?.model ?? e.assign,
          subtitle: e.triggerAt,
          summary: e.summary,
          table: expects.length === 0 ? undefined : {
            columns: [
              { key: "metric", label: "指标" },
              { key: "expected", label: "事前预期" },
              ...(expects.some((row) => row.actual) ? [{ key: "actual", label: "实际兑现" }] : []),
              { key: "status", label: "判定" },
            ],
            rows: expects.map((row) => ({
              metric: row.label,
              expected: row.expected,
              actual: row.actual,
              status: { text: VERDICT_FALLBACK[row.status] || row.status, tone: VERDICT_TONE[row.status] },
            })),
          },
        };
      }),
    };
  },
};
