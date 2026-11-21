import { schema, initial } from "./schema.js";
import { views } from "./views.js";

export default defineTemplate({
  id: "equity-research",
  title: "个股长期投研",
  description: "方案 B 混合 VO 范式：无框沉浸 HTML 价格区间轴 + 原生 React 事件时间线（Expect 核查）+ 常青深度正文。",
  inputs: {
    ticker: { type: "string", default: "NVDA", label: "标的代码" },
    reviewModel: { type: "string", default: "gpt-6.2-soar", label: "异动独立复核模型" },
  },
  schedules: [{
    id: "weekly-price-refresh", cron: "30 8 * * 1", timezone: "America/New_York",
    title: "每周价格与估值复核",
    prompt: "刷新 ${inputs.ticker} 现价与同行估值快照，用 data_patch 更新 valuation；检查价格异动与临近 events，必要时通过 schedule_set 安排单次 Expect 核查。",
  }],
  setup(inputs) {
    return {
      data: {
        schema,
        initial: () => initial(inputs),
      },
      report: {
        humanReadOnly: true,
        template: "./report.md",
        views,
      },
      tasks: {
        routing: [
          { when: { role: "earnings" }, model: "claude-opus-4", access: "read_only" },
          { when: { role: "event_review" }, model: inputs.reviewModel, access: "read_only", enforce: true },
        ],
      },
    };
  },
});
