import { schema, initial } from "./schema.js";
import { views } from "./views.js";

export default defineTemplate({
  id: "okr-loop",
  title: "长期 OKR 优化闭环",
  description: "稳定 Objective + 混合型四列 KR 总账（内核自动更新时间）+ 非时间序分组 Loop Roadmap（局部 Expect 与失败回滚）。",
  inputs: {
    checkCmd: { type: "string", default: "python scripts/ci_daily_check.py", label: "每日指标采集脚本" },
    triageModel: { type: "string", default: "gpt-6.2-soar", label: "异常归因与回滚模型" },
  },
  schedules: [{
    id: "daily-ci-check", cron: "0 9 * * *", timezone: "Asia/Shanghai",
    title: "每日 CI 指标采集",
    prompt: "运行 ${inputs.checkCmd}，采集过去 24h CI 指标，用 data_patch 刷新 KR 总账；检查指标缺口并决定是否归因、回滚或重排 Roadmap。",
  }],
  setup(inputs) {
    return {
      data: {
        schema,
        initial: () => initial(),
      },
      report: {
        humanReadOnly: true,
        template: "./report.md",
        views,
      },
      tasks: {
        routing: [
          { when: { role: "triage" }, model: inputs.triageModel, access: "read_only", enforce: true },
          { when: { role: "optimize" }, model: "claude-opus-4", access: "read_write" },
        ],
      },
    };
  },
});
