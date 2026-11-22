/** @param {import("../perga-template.d.ts").SchemaBuilder} t */
export const schema = (t) => ({
  ticker: t.string(),
  valuation: t.object({
    unit: t.string(),
    currentPrice: t.number().optional(),
    currentLabel: t.string().optional(),
    targets: t.array(t.object({
      price: t.number(),
      emphasis: t.boolean().optional(),
      assumption: t.string(),
      docRef: t.ref("file").optional(),
    })),
  }),
  events: t.array(t.object({
    id: t.id(),
    date: t.isoDate(),
    title: t.string(),
    verdict: t.enum(["pending", "beat", "inline", "miss"]),
    verdictLabel: t.string().optional(),
    status: t.enum(["pending", "running", "done", "blocked"]).optional(),
    taskRef: t.ref("task").optional(),
    assign: t.string().optional(),
    assignKind: t.enum(["model", "cli"]).optional(),
    triggerAt: t.string().optional(),
    summary: t.markdown().optional(),
    expects: t.array(t.object({
      label: t.string(),
      expected: t.string(),
      actual: t.string().optional(),
      status: t.enum(["pending", "beat", "inline", "miss"]),
    })).optional(),
  })),
});

/** @param {Record<string, any>} inputs */
export const initial = (inputs) => ({
  ticker: inputs.ticker,
  valuation: {
    unit: "$",
    targets: [],
  },
  events: [],
});
