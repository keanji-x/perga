/**
 * @param {Record<string, any>} inputs
 * @returns {import("../perga-template.d.ts").TriggerSpec[]}
 */
export function createTriggers(inputs) {
  return [
    {
      id: "daily-ci-check",
      label: "每日 09:00 采集过去 24h CI 指标并刷新 KR 总账",
      on: { kind: "cron", expr: "0 9 * * *", timezone: "Asia/Shanghai" },
      action: {
        kind: "run_cli",
        command: inputs.checkCmd,
        writeDataPath: "krs",
      },
    },
    {
      id: "kr-breach-triage",
      label: "成功率 < 98.5% 或 Flaky > 1.0% 时自动归因并评估回滚",
      on: { kind: "data_change", path: "krs" },
      when: ({ data }) =>
        (data.krs.find((/** @type {any} */ k) => k.code === "KR2")?.numericValue ?? 100) < 98.5 ||
        (data.krs.find((/** @type {any} */ k) => k.code === "KR3")?.numericValue ?? 0) > 1.0,
      action: {
        kind: "dispatch_worker",
        role: "triage",
        goal: "定位过去 24h Top3 失败/超时用例，判断最近优化 Loop 是否需要回滚，必要时通过 perga_data_patch 增补护栏 KR。",
      },
    },
    {
      id: "replan-roadmap",
      label: "当前优化 Loop 结算后若 KR 仍有缺口则重排 Roadmap",
      on: { kind: "task_settled", role: "optimize" },
      when: ({ data }) => data.krs.some((/** @type {any} */ k) => k.status !== "met"),
      action: {
        kind: "wake_lead",
        prompt: "当前优化 Loop 已结算但仍有未达标 KR：请对照最新指标缺口重排下一轮 Roadmap 优先级并决定是否触发 P1。",
      },
    },
  ];
}
