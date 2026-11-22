import { schema, initial } from "./schema.js";
import { views } from "./views.js";

export default defineTemplate({
  id: "equity-research",
  title: "个股长期投研",
  description: "跟踪价格区间、估值和临近事件，持续更新长期投资判断。",
  inputs: {
    ticker: { type: "string", default: "NVDA", label: "标的代码" },
    reviewModel: { type: "string", default: "gpt-6.2-soar", label: "异动独立复核模型" },
  },
  flow: [{
    id: "weekly-price-refresh",
    title: "每周价格与估值复核",
    when: { cron: "30 8 * * 1", tz: "America/New_York" },
    do: {
      kind: "wake",
      session: "lead",
      prompt: "刷新 ${inputs.ticker} 现价与同行估值快照，用 data_patch 更新 valuation；检查价格异动与临近 events，必要时在 flow.json 中安排单次 at 核查节点。",
    },
  }],
  setup(inputs) {
    return {
      data: {
        schema,
        initial: () => initial(inputs),
      },
      report: {
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
