import { schema, initial } from "./schema.js";
import { createViews } from "./views.js";

export default defineTemplate({
  id: "agents-delivery",
  title: "AGENTS.md 阶段交付协议",
  description: "并行双路审查与修复闭环：极简 Target + 无框 SVG 交付拓扑（支持并行 Review 与打回 Loop）+ 模型路由强约束。",
  inputs: {
    reviewModel: { type: "string", default: "gpt-6.2-soar", label: "审查强制模型" },
    testCmd: { type: "string", default: "dotnet test Perga.sln", label: "全量回归门禁命令" },
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
        views: createViews(),
      },
      tasks: {
        requireGateForReadWrite: true,
        routing: [
          { when: { role: "review" }, model: inputs.reviewModel, access: "read_only", enforce: true },
          { when: { role: "impl" }, access: "read_write" },
        ],
      },
    };
  },
});
