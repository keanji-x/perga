/** @param {import("../perga-template.d.ts").SchemaBuilder} t */
export const schema = (t) => ({
  krs: t.array(t.object({
    code: t.string(),
    label: t.markdown(),
    tag: t.string().optional(),
    current: t.string(),
    numericValue: t.number().optional(),
    targetLabel: t.string(),
    status: t.enum(["met", "on_track", "at_risk"]),
    updatedAtDisplay: t.string().optional(),
  })),
  loops: t.array(t.object({
    id: t.id(),
    title: t.string(),
    state: t.enum(["active", "queued", "closed", "rolled_back"]),
    priority: t.enum(["P0", "P1", "P2"]).optional(),
    date: t.string().optional(),
    status: t.enum(["pending", "running", "done", "blocked"]).optional(),
    verdict: t.string().optional(),
    taskRef: t.ref("task").optional(),
    assign: t.string().optional(),
    assignKind: t.enum(["model", "cli"]).optional(),
    triggerAt: t.string().optional(),
    summary: t.markdown().optional(),
    localExpects: t.array(t.object({
      label: t.string(),
      expected: t.string(),
      actual: t.string().optional(),
      status: t.enum(["pending", "beat", "inline", "miss"]),
    })).optional(),
  })),
});

export const initial = () => ({
  krs: [
    { code: "KR1", label: "主干 CI P95 执行时长", current: "14.8 min", numericValue: 14.8, targetLabel: "≤ 8.0 min", status: "at_risk" },
    { code: "KR2", label: "过去 24h CI 成功率", current: "96.4%", numericValue: 96.4, targetLabel: "≥ 98.5%", status: "at_risk" },
  ],
  loops: [],
});
