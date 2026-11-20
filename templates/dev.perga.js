// @ts-check
/// <reference path="./perga-template.d.ts" />

/**
 * @typedef {Object} DevInputs
 * @property {string} baseBranch
 * @property {string} testCmd
 * @property {boolean} requirePrReview
 * @property {"low" | "medium" | "high"} riskLevel
 */

/**
 * @typedef {Object} DevState
 * @property {number} iteration
 * @property {string[]} completedTasks
 * @property {number} failedAttempts
 * @property {string | null} latestChangeId
 * @property {string | null} rollbackOpId
 * @property {boolean} ciVerified
 * @property {boolean} prPublished
 */

/** @type {import("./perga-template.d.ts").PergaTemplateDefinition<DevState, DevInputs>} */
const devTemplate = {
  id: "dev",
  title: "Software Engineering Track",
  description:
    "End-to-end engineering workflow with isolated Jujutsu/Git workspaces, mandatory read-write verification gates, and PR merge safeguards.",
  plugins: [],
  inputs: {
    baseBranch: {
      type: "string",
      default: "main",
      label: "Upstream base branch"
    },
    testCmd: {
      type: "string",
      default: "dotnet test",
      label: "Default verification command"
    },
    requirePrReview: {
      type: "boolean",
      default: true,
      label: "Require human confirmation before merging PR"
    },
    riskLevel: {
      type: "enum",
      values: ["low", "medium", "high"],
      default: "medium",
      label: "Change risk profile"
    }
  },
  initialState: (_inputs) => ({
    iteration: 0,
    completedTasks: [],
    failedAttempts: 0,
    latestChangeId: null,
    rollbackOpId: null,
    ciVerified: false,
    prPublished: false
  }),
  headerView: (ctx) => ({
    badges: [{ label: ctx.inputs.riskLevel + " risk", tone: ctx.inputs.riskLevel === "high" ? "warn" : "neutral" }],
    notice: ctx.state.iteration === 0 ? { text: "Implementation and verification pending.", tone: "info" } : undefined,
    metrics: [
      { label: "Completed tasks", value: String(ctx.state.completedTasks.length) },
      { label: "CI", value: ctx.state.ciVerified ? "Verified" : "Pending", tone: ctx.state.ciVerified ? "success" : "warn" }
    ],
    rules: [{ label: "Delivery", text: "Read-write tasks require a gate. Verify CI before merging." }]
  }),
  workspace: {
    driver: "vcs-workspace",
    lowerCacheDirs: ["node_modules", "target", ".nuget"],
    onCreateCmd: "jj status 2>/dev/null || git status --short",
    onCloseCmd: "jj status 2>/dev/null || true"
  },
  report: {
    maxProseChars: 24000,
    sections: [
      { h1: "Objective", editableBy: ["human", "lead"] },
      { h1: "Architecture & Plan", editableBy: ["human", "lead", "subagent"] },
      { h1: "Tasks", editableBy: ["human", "lead"] },
      {
        h1: "Verification & Delivery",
        omitIfEmpty: true,
        editableBy: ["human", "lead", "subagent", "system"]
      }
    ]
  },
  tasks: {
    dispatch: { section: "Tasks", access: "read_write", gateInput: "testCmd" },
    requireGateForReadWrite: true,
    afterWorkerDoneCmd:
      "jj log -r @ --no-graph -T '{\"changeId\":\"' ++ change_id.short() ++ '\",\"commitId\":\"' ++ commit_id.short() ++ '\"}' 2>/dev/null || echo '{}'"
  },
  turnContext: (ctx) => {
    const session = ctx.session;
    const roleLabel = session.isLead
      ? "Lead"
      : session.boundAttemptId
        ? `Contract Subagent (${session.boundAttemptId})`
        : "Collaborative Subagent";
    const lines = [
      `[Dev Policy] Role: ${roleLabel} | Base: ${ctx.inputs.baseBranch} | Test Gate: ${ctx.inputs.testCmd}`,
      `[Dev State] Iteration: ${ctx.state.iteration} | Completed Tasks: ${ctx.state.completedTasks.length} | CI Verified: ${ctx.state.ciVerified} | Latest Change: ${ctx.state.latestChangeId ?? "none"}`
    ];
    if (session.contextUsageRatio >= 0.75) {
      lines.push(
        "[Context Notice] Session context usage is high; keep findings concise in the Report so Epoch handover stays lossless."
      );
    }
    return lines.join("\n");
  },
  after: (event, ctx) => {
    if (event.type === "schedule.fired" && event.scheduleId === "upstream-drift-check")
      return `${event.prompt} (completed tasks: ${ctx.state.completedTasks.length}).`;
    if (event.type !== "task.settled") return;
    ctx.state.iteration += 1;

    if (typeof event.meta?.changeId === "string" && event.meta.changeId.length > 0) {
      ctx.state.latestChangeId = event.meta.changeId;
    }
    if (typeof event.meta?.opId === "string" && event.meta.opId.length > 0) {
      ctx.state.rollbackOpId = event.meta.opId;
    }

    if (event.attemptStatus === "done") {
      if (!ctx.state.completedTasks.includes(event.taskKey)) {
        ctx.state.completedTasks.push(event.taskKey);
      }
      const gatePassed =
        event.gateVerdict === undefined ||
        event.gateVerdict === null ||
        event.gateVerdict.passed !== false;
      if (event.accessMode === "read_write" && gatePassed) {
        ctx.state.ciVerified = true;
      }
      return `Task '${event.taskKey}' completed (change=${ctx.state.latestChangeId ?? "n/a"}, verified=${ctx.state.ciVerified}).`;
    }

    if (event.attemptStatus === "failed") {
      ctx.state.failedAttempts += 1;
      if (event.accessMode === "read_write") {
        ctx.state.ciVerified = false;
      }
      return `Task '${event.taskKey}' failed (attempt failures=${ctx.state.failedAttempts}). Inspect gate diagnostics before re-dispatching.`;
    }

    return `Task '${event.taskKey}' settled with status '${event.attemptStatus}'.`;
  },
  before: (action, ctx) => {
    if (action.type === "report.commit") {
      const info = action;
      if (info.nextMarkdown.trim().length === 0) {
        return Deny("Engineering report cannot be empty.");
      }
      if (
        info.touchedSections.includes("Objective") &&
        !/#\s+Objective\b/i.test(info.nextMarkdown)
      ) {
        return Deny("The '# Objective' section header must remain present in the report.");
      }
      return Allow();
    }
    if (!action.type.startsWith("tool.")) return Allow();
    const call = action;
    if (call.mcpServer === "vcs" && call.toolName === "vcs_pr_merge") {
      if (!ctx.state.ciVerified) {
        return Deny(
          "Cannot merge PR before at least one read-write task gate has passed verification (ciVerified=false)."
        );
      }
      if (ctx.inputs.requirePrReview) {
        return AskHuman(
          `Approve merging PR into '${ctx.inputs.baseBranch}'?`,
          { approve: "Approve & Merge", deny: "Hold for Manual Review" }
        );
      }
      return Allow();
    }

    if (call.mcpServer === "vcs" && call.toolName === "vcs_publish_pr") {
      if (ctx.inputs.riskLevel === "high" && !ctx.state.prPublished) {
        ctx.state.prPublished = true;
        return AskHuman(
          `High-risk track: publish PR against '${ctx.inputs.baseBranch}' now?`,
          { approve: "Publish PR", deny: "Cancel" }
        );
      }
      ctx.state.prPublished = true;
      return Allow();
    }

    return Allow();
  },
  schedules: [
    {
      id: "upstream-drift-check",
      cron: "0 9 * * 1-5",
      timezone: "UTC",
      prompt: "Check upstream branch '${inputs.baseBranch}' for new commits and verify '${inputs.testCmd}' still passes."
    }
  ]
};

export default defineTemplate(devTemplate);
