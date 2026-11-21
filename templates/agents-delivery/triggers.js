/**
 * @param {Record<string, any>} inputs
 * @returns {import("../perga-template.d.ts").TriggerSpec[]}
 */
export function createTriggers(inputs) {
  return [
    {
      id: "auto-l2-review",
      label: "实现二与 L1 Review 完成后自动派发 L2 独立审查",
      on: { kind: "task_settled" },
      when: ({ kernel }) =>
        kernel.tasks["impl-2"]?.status === "done" &&
        kernel.tasks["review-l1"]?.status === "done" &&
        !kernel.tasks["review-l2"],
      action: {
        kind: "dispatch_worker",
        role: "review",
        taskKey: "review-l2",
        bindRef: "stages[review-l2].taskRef",
        goal: "执行第二轮独立审查（全量 Diff 与边界复核）并回写 stages[review-l2].summary。",
      },
    },
    {
      id: "final-regression",
      label: "L2 Review 通过后运行全量回归门禁",
      on: { kind: "task_settled", taskKey: "review-l2" },
      when: ({ event }) => event.status === "done",
      action: {
        kind: "run_cli",
        command: inputs.testCmd,
      },
    },
  ];
}
