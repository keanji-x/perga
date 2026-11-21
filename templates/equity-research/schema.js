/** @param {import("../perga-template.d.ts").SchemaBuilder} t */
export const schema = (t) => ({
  ticker: t.string(),
  valuation: t.object({
    unit: t.string(),
    currentPrice: t.number(),
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
    currentPrice: 137.5,
    currentLabel: "现价",
    targets: [
      { price: 118, assumption: "云厂 CapEx 放缓、ASIC 分流加速，毛利率回落至 70%" },
      { price: 172, emphasis: true, assumption: "Blackwell / Rubin 按期放量，DC 收入维持 ~35% 增速" },
      { price: 225, assumption: "推理 Token 需求指数级扩张，主权 AI 与物理 AI 放量" },
    ],
  },
  events: [],
});
