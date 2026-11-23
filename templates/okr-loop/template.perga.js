import { schema, initial } from "./schema.js";
import { views } from "./views.js";

export default defineTemplate({
  id: "okr-loop",
  title: "长期 OKR 优化闭环",
  description: "跟踪关键结果，在每日指标采集后核对进度和异常。",
  inputs: {
    optimizeProvider: { type: "string", optional: true, label: "改进执行提供方" },
    optimizeModel: { type: "string", optional: true, label: "改进执行模型" },
    checkCmd: { type: "string", default: "python scripts/ci_daily_check.py", label: "每日指标采集脚本" },
    triageProvider: { type: "string", optional: true, label: "异常归因与回滚提供方" },
    triageModel: { type: "string", optional: true, label: "异常归因与回滚模型" },
  },
  flow: [{
    id: "daily-ci-check",
    title: "每日 CI 指标采集",
    when: { cron: "0 9 * * *", tz: "Asia/Shanghai" },
    do: {
      kind: "wake",
      session: "lead",
      prompt: "检查 ${inputs.checkCmd} 是否可用。可用时采集过去 24 小时 CI 指标，用 data_patch 更新 KR 总账，并核对异常。",
    },
  }],
  data: { schema, initial },
  report: {
    template: "./report.md",
    views,
  },
  tasks: {
    routing: [
      {
        when: { role: "triage" },
        provider: "${inputs.triageProvider}",
        model: "${inputs.triageModel}",
        access: "read_only", enforce: true,
      },
      {
        when: { role: "optimize" },
        provider: "${inputs.optimizeProvider}",
        model: "${inputs.optimizeModel}",
        access: "read_write",
      },
    ],
  },
});
