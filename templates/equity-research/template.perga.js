import { schema, initial } from "./schema.js";
import { views } from "./views.js";

export default defineTemplate({
  id: "equity-research",
  title: "个股长期投研",
  description: "为单只上市股票建立并持续更新投资判断，跟踪价格、估值、财报与催化事件。",
  inputs: {
    earningsProvider: { type: "string", optional: true, label: "财报分析提供方" },
    earningsModel: { type: "string", optional: true, label: "财报分析模型" },
    ticker: { type: "string", default: "NVDA", label: "标的代码" },
    reviewProvider: { type: "string", optional: true, label: "异动独立复核提供方" },
    reviewModel: { type: "string", optional: true, label: "异动独立复核模型" },
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
  data: { schema, initial },
  report: {
    template: "./report.md",
    views,
  },
  tasks: {
    routing: [
      {
        when: { role: "earnings" },
        provider: "${inputs.earningsProvider}",
        model: "${inputs.earningsModel}",
        access: "read_only",
      },
      {
        when: { role: "event_review" },
        provider: "${inputs.reviewProvider}",
        model: "${inputs.reviewModel}",
        access: "read_only", enforce: true,
      },
    ],
  },
});
