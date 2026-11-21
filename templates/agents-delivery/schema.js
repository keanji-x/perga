/** @param {import("../perga-template.d.ts").SchemaBuilder} t */
export const schema = (t) => {
  const gateCheck = t.object({
    label: t.string(),
    status: t.enum(["pass", "running", "fail", "pending"]),
    detail: t.string().optional(),
    command: t.string().optional(),
    output: t.string().optional(),
  });

  const stageNode = t.object({
    title: t.string(),
    status: t.enum(["pending", "running", "done", "blocked"]),
    taskRef: t.ref("task").optional(),
    assign: t.string().optional(),
    assignKind: t.enum(["model", "cli"]).optional(),
    summary: t.markdown().optional(),
    docRef: t.ref("file").optional(),
    docLabel: t.string().optional(),
    triggerAt: t.string().optional(),
    checks: t.array(gateCheck).optional(),
  });

  const reviewLane = t.object({
    id: t.id(),
    title: t.string(),
    scope: t.string().optional(),
    status: t.enum(["pending", "running", "done", "blocked"]),
    verdict: t.enum(["pending", "pass", "changes_requested"]).optional(),
    round: t.number().optional(),
    taskRef: t.ref("task").optional(),
    assign: t.string().optional(),
    summary: t.markdown().optional(),
    blockers: t.array(t.string()).optional(),
    docRef: t.ref("file").optional(),
    docLabel: t.string().optional(),
    checks: t.array(gateCheck).optional(),
  });

  const loopRound = t.object({
    round: t.number(),
    label: t.string(),
    outcome: t.enum(["running", "changes_requested", "pass"]),
    triggeredBy: t.string().optional(),
    summary: t.string(),
  });

  return {
    reviewTier: t.enum(["L0", "L1", "L2"]),
    currentRound: t.number(),
    design: stageNode,
    implement: stageNode,
    reviews: t.array(reviewLane),
    merge: stageNode,
    rounds: t.array(loopRound).optional(),
  };
};

/** @param {Record<string, any>} inputs */
export const initial = (inputs) => ({
  reviewTier: "L2",
  currentRound: 1,
  design: {
    title: "设计",
    status: "pending",
    assign: "claude-opus-4",
    assignKind: "model",
    summary: "明确单一结果、契约边界与验证口径。",
  },
  implement: {
    title: "实现与修复",
    status: "pending",
    assign: "claude-opus-4",
    assignKind: "model",
    summary: "按设计落地实现，并在 Review 打回后进入下一轮定向修复。",
  },
  reviews: [
    {
      id: "review-a",
      title: "Review A · 后端与安全",
      scope: "权威边界 / 持久化 / 沙箱",
      status: "pending",
      verdict: "pending",
      round: 1,
      assign: inputs.reviewModel,
      summary: "独立审查内核状态机、持久化顺序与安全边界。",
    },
    {
      id: "review-b",
      title: "Review B · 前端与契约",
      scope: "Wire 契约 / Bridge / 交互",
      status: "pending",
      verdict: "pending",
      round: 1,
      assign: inputs.reviewModel,
      summary: "独立审查前后端契约一致、沙箱隔离与空态边界。",
    },
  ],
  merge: {
    title: "验证合入",
    status: "pending",
    assign: inputs.testCmd,
    assignKind: "cli",
    triggerAt: "双路 Review 均通过且全量回归门禁绿灯后合入",
    summary: "全量测试与构建验证通过后发布 PR 并合入主干。",
  },
  rounds: [
    {
      round: 1,
      label: "R1 首次提交",
      outcome: "running",
      summary: "首轮实现完成后并行触发双路独立 Review。",
    },
  ],
});
