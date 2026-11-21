import { schema, initial } from "./schema.js";
import { views } from "./views.js";
import { createTriggers } from "./triggers.js";

export default defineTemplate({
  id: "equity-research",
  title: "个股长期投研",
  description: "方案 B 混合 VO 范式：无框沉浸 HTML 价格区间轴 + 原生 React 事件时间线（Expect 核查）+ 常青深度正文。",
  inputs: {
    ticker: { type: "string", default: "NVDA", label: "标的代码" },
    reviewModel: { type: "string", default: "gpt-6.2-soar", label: "异动独立复核模型" },
  },
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
      triggers: createTriggers(inputs),
    };
  },
});
