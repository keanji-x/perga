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
});

export const initial = () => ({
  krs: [
    { code: "KR1", label: "主干 CI P95 执行时长", current: "14.8 min", numericValue: 14.8, targetLabel: "≤ 8.0 min", status: "at_risk" },
    { code: "KR2", label: "过去 24h CI 成功率", current: "96.4%", numericValue: 96.4, targetLabel: "≥ 98.5%", status: "at_risk" },
  ],
});
