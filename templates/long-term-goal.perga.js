// @ts-check
/// <reference path="./perga-template.d.ts" />

/**
 * @typedef {Object} LongTermGoalInputs
 * @property {"daily" | "weekly" | "biweekly"} cadence
 * @property {string} [targetMetric]
 * @property {boolean} autoDispatchTasks
 */

/**
 * @typedef {Object} LongTermGoalState
 * @property {number} checkInCount
 * @property {number} milestonesCompleted
 * @property {number} consecutiveFailures
 * @property {string | null} lastSettledTask
 * @property {boolean} pausedForHuman
 */

/** @type {import("./perga-template.d.ts").PergaTemplateDefinition<LongTermGoalState, LongTermGoalInputs>} */
const longTermGoalTemplate = {
  id: "long-term-goal",
  title: "Long-Term Goal & Milestone Track",
  description:
    "Sustained multi-week goal execution with scheduled check-ins, milestone tracking, failure circuit-breakers, and structured retrospectives.",
  plugins: [],
  inputs: {
    cadence: {
      type: "enum",
      values: ["daily", "weekly", "biweekly"],
      default: "weekly",
      label: "Check-in cadence"
    },
    targetMetric: {
      type: "string",
      optional: true,
      default: "100% milestone completion",
      label: "Primary success metric"
    },
    autoDispatchTasks: {
      type: "boolean",
      default: true,
      label: "Allow autonomous task progression between check-ins"
    }
  },
  initialState: (_inputs) => ({
    checkInCount: 0,
    milestonesCompleted: 0,
    consecutiveFailures: 0,
    lastSettledTask: null,
    pausedForHuman: false
  }),
  headerView: (ctx) => ({
    badges: [{ label: ctx.inputs.cadence + " check-in", tone: "accent" }],
    notice: ctx.state.pausedForHuman
      ? { text: "Autonomous dispatch paused after repeated failures. Human guidance needed.", tone: "warn" }
      : ctx.state.checkInCount === 0 ? { text: "Awaiting the first progress check-in.", tone: "info" } : undefined,
    metrics: [
      { label: "Check-ins", value: String(ctx.state.checkInCount) },
      { label: "Completed milestones", value: String(ctx.state.milestonesCompleted) },
      { label: "Consecutive failures", value: String(ctx.state.consecutiveFailures) }
    ],
    rules: [{ label: "Progress", text: "Log measurable progress at each check-in. Three consecutive failures pause autonomous dispatch." }]
  }),
  workspace: {
    driver: "direct"
  },
  report: {
    maxProseChars: 32000,
    sections: [
      { h1: "North Star", editableBy: ["human", "lead"] },
      { h1: "Milestones", editableBy: ["human", "lead"] },
      { h1: "Active Tasks", editableBy: ["human", "lead"] },
      {
        h1: "Progress Log",
        editableBy: ["human", "lead", "subagent", "system"]
      },
      {
        h1: "Risks & Blockers",
        omitIfEmpty: true,
        editableBy: ["human", "lead", "subagent"]
      }
    ]
  },
  tasks: {
    requireGateForReadWrite: false
  },
  turnContext: (ctx) => {
    const metric = ctx.inputs.targetMetric ?? "unspecified";
    return [
      `[Long-Term Goal] Cadence: ${ctx.inputs.cadence} | Target Metric: ${metric} | Auto-Dispatch: ${ctx.inputs.autoDispatchTasks}`,
      `[Progress] Check-ins: ${ctx.state.checkInCount} | Completed Tasks: ${ctx.state.milestonesCompleted} | Consecutive Failures: ${ctx.state.consecutiveFailures} | Paused: ${ctx.state.pausedForHuman}`
    ].join("\n");
  },
  after: (event, ctx) => {
    if (event.type === "schedule.fired") {
      if (event.scheduleId !== "cadence-checkin") return event.prompt;
      ctx.state.checkInCount += 1;
      return `${event.prompt} Check-in #${ctx.state.checkInCount}.`;
    }
    if (event.type !== "task.settled") return;
    ctx.state.lastSettledTask = event.taskKey;

    if (event.attemptStatus === "done") {
      ctx.state.milestonesCompleted += 1;
      ctx.state.consecutiveFailures = 0;
      return `Milestone task '${event.taskKey}' succeeded (completed=${ctx.state.milestonesCompleted}). Update '# Progress Log' and evaluate next milestone.`;
    }

    if (event.attemptStatus === "failed") {
      ctx.state.consecutiveFailures += 1;
      if (ctx.state.consecutiveFailures >= 3) {
        ctx.state.pausedForHuman = true;
        return `Task '${event.taskKey}' failed (${ctx.state.consecutiveFailures} consecutive failures). Autonomous dispatch paused; request human guidance.`;
      }
      return `Task '${event.taskKey}' failed (${ctx.state.consecutiveFailures}/3 before circuit breaker).`;
    }

    return `Task '${event.taskKey}' settled as '${event.attemptStatus}'.`;
  },
  before: (action, ctx) => {
    if (action.type === "report.commit") {
      const info = action;
      if (
        info.touchedSections.includes("North Star") &&
        info.editorRole.toLowerCase() !== "human" &&
        info.editorRole.toLowerCase() !== "lead"
      ) {
        return Deny("Only human or lead sessions may revise the '# North Star' definition.");
      }
      if (info.nextMarkdown.trim().length < 16) {
        return Deny("Long-term goal report must retain structured milestone content.");
      }
      return Allow();
    }
    if (!action.type.startsWith("tool.")) return Allow();
    const call = action;
    if (ctx.state.pausedForHuman && call.mcpServer === "perga" && call.toolName === "perga_mail_send") {
      return Deny("Track is paused after repeated task failures; resolve blockers with the user before sending cross-track mail.");
    }

    if (call.mcpServer === "perga" && call.toolName === "perga_track_close") {
      if (ctx.state.milestonesCompleted === 0) {
        return AskHuman(
          "Close long-term goal track before any milestone task has completed?",
          ["Close Track Anyway", "Keep Track Open"]
        );
      }
      return AskHuman(
        `Confirm closing long-term goal track (${ctx.state.milestonesCompleted} tasks completed)?`,
        ["Confirm Close", "Continue Working"]
      );
    }

    return Allow();
  },
  schedules: [
    {
      id: "cadence-checkin",
      cron: "0 9 * * 1",
      timezone: "UTC",
      prompt: "Scheduled ${inputs.cadence} check-in: review '${inputs.targetMetric}', log measurable progress, and refresh '# Active Tasks'."
    },
    {
      id: "friday-retrospective",
      cron: "0 17 * * 5",
      timezone: "UTC",
      prompt:
        "Run end-of-week retrospective: summarize accomplishments in '# Progress Log' and surface any emerging blockers in '# Risks & Blockers'."
    }
  ]
};

export default defineTemplate(longTermGoalTemplate);
