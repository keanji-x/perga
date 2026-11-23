export default defineTemplate({
  id: "agents-delivery",
  title: "AGENTS.md 阶段交付协议",
  description: "按仓库 AGENTS.md 组织实现、独立审查与验证。",
  inputs: {
    reviewProvider: { type: "string", optional: true, label: "审查提供方" },
    reviewModel: { type: "string", optional: true, label: "审查模型" },
    testCmd: { type: "string", default: "dotnet test Perga.sln", label: "全量回归门禁命令" },
  },
  report: {
    template: "./report.md",
  },
  tasks: {
    requireGateForReadWrite: true,
    routing: [
      {
        when: { role: "review" },
        provider: "${inputs.reviewProvider}",
        model: "${inputs.reviewModel}",
        access: "read_only", enforce: true,
      },
      { when: { role: "impl" }, access: "read_write" },
    ],
  },
  turnContext({ inputs, session }) {
    if (!session.isLead) return;
    return "明确交付目标后，再用 Flow 安排实现、独立审查和验证。为写入任务配置门禁，默认验证命令为 " + inputs.testCmd + "。";
  },
});
