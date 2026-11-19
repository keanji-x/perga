// @ts-check
/// <reference path="./perga-template.d.ts" />

/**
 * @typedef {Object} InvestmentResearchInputs
 * @property {string} tickerOrTheme
 * @property {number} horizonMonths
 * @property {string} benchmark
 * @property {boolean} allowPaperOrders
 */

/**
 * @typedef {Object} InvestmentResearchState
 * @property {number} dataSourcesVerified
 * @property {string[]} completedAnalyses
 * @property {number} paperOrdersSubmitted
 * @property {number | null} convictionScore
 */

/** @type {import("./perga-template.d.ts").PergaTemplateDefinition<InvestmentResearchState, InvestmentResearchInputs>} */
const investmentResearchTemplate = {
  id: "investment-research",
  title: "Investment & Factor Research Track",
  description:
    "Institutional equity, macro, and Barra factor research workflow with primary-source archival, risk attribution, and gated paper-trading execution.",
  plugins: ["market", "barra", "invest", "paper-trading", "research-archive"],
  inputs: {
    tickerOrTheme: {
      type: "string",
      default: "GLOBAL-MACRO",
      label: "Target ticker, sector, or macro theme"
    },
    horizonMonths: {
      type: "number",
      default: 12,
      label: "Investment horizon (months)"
    },
    benchmark: {
      type: "string",
      default: "CSI300",
      label: "Performance & factor benchmark"
    },
    allowPaperOrders: {
      type: "boolean",
      default: false,
      label: "Enable paper-trading portfolio order placement"
    }
  },
  initialState: (_inputs) => ({
    dataSourcesVerified: 0,
    completedAnalyses: [],
    paperOrdersSubmitted: 0,
    convictionScore: null
  }),
  headerView: (ctx) => ({
    badges: [{ label: ctx.inputs.tickerOrTheme, tone: "accent" }, { label: ctx.inputs.allowPaperOrders ? "Paper orders enabled" : "Research only" }],
    notice: ctx.state.dataSourcesVerified === 0 ? { text: "Awaiting verified research sources.", tone: "info" } : undefined,
    metrics: [
      { label: "Verified sources", value: String(ctx.state.dataSourcesVerified) },
      { label: "Completed analyses", value: String(ctx.state.completedAnalyses.length) },
      { label: "Conviction", value: ctx.state.convictionScore === null ? "Unrated" : `${ctx.state.convictionScore}/100` }
    ],
    rules: [{ label: "Evidence", text: "Archive sources and verify data before submitting paper orders." }]
  }),
  workspace: {
    driver: "direct"
  },
  report: {
    maxProseChars: 40000,
    sections: [
      { h1: "Executive Thesis", editableBy: ["human", "lead"] },
      {
        h1: "Catalysts & Valuation",
        editableBy: ["human", "lead", "subagent"]
      },
      {
        h1: "Factor & Risk Exposure",
        editableBy: ["human", "lead", "subagent"]
      },
      { h1: "Research Tasks", editableBy: ["human", "lead"] },
      {
        h1: "Citations & Archive",
        omitIfEmpty: true,
        editableBy: ["human", "lead", "subagent", "system"]
      }
    ]
  },
  tasks: {
    requireGateForReadWrite: false
  },
  turnContext: (ctx) => {
    const session = ctx.session;
    const conviction =
      ctx.state.convictionScore === null ? "unrated" : `${ctx.state.convictionScore}/100`;
    return [
      `[Investment Research] Subject: ${ctx.inputs.tickerOrTheme} | Horizon: ${ctx.inputs.horizonMonths}m | Benchmark: ${ctx.inputs.benchmark}`,
      `[Research State] Verified Sources: ${ctx.state.dataSourcesVerified} | Completed Analyses: ${ctx.state.completedAnalyses.length} | Conviction: ${conviction} | Paper Orders Allowed: ${ctx.inputs.allowPaperOrders}`,
      session.isLead
        ? "[Discipline] Ground every thesis claim in archived primary sources and Barra factor attribution."
        : "[Subagent Scope] Attach structured citations and numeric evidence to worker results."
    ].join("\n");
  },
  after: (event, ctx) => {
    if (event.type !== "task.settled") return;
    if (event.attemptStatus === "done") {
      if (!ctx.state.completedAnalyses.includes(event.taskKey)) {
        ctx.state.completedAnalyses.push(event.taskKey);
      }
      const verifiedDelta =
        typeof event.workerResult?.verifiedSources === "number"
          ? event.workerResult.verifiedSources
          : 1;
      ctx.state.dataSourcesVerified += verifiedDelta;

      if (typeof event.workerResult?.convictionScore === "number") {
        ctx.state.convictionScore = event.workerResult.convictionScore;
      }

      return `Research task '${event.taskKey}' completed for ${ctx.inputs.tickerOrTheme} (verified sources=${ctx.state.dataSourcesVerified}). Synthesize findings into the report.`;
    }

    if (event.attemptStatus === "failed") {
      return `Research task '${event.taskKey}' failed; cross-check data feed availability before retrying.`;
    }

    return undefined;
  },
  before: (action, ctx) => {
    if (action.type === "report.commit") {
      const info = action;
      if (
        info.touchedSections.includes("Executive Thesis") &&
        info.nextMarkdown.includes("TODO:UNVERIFIED")
      ) {
        return Deny(
          "Cannot publish 'TODO:UNVERIFIED' placeholder figures inside '# Executive Thesis'; verify primary data first."
        );
      }
      return Allow();
    }
    if (!action.type.startsWith("tool.")) return Allow();
    const call = action;
    if (call.mcpServer === "paper-trading") {
      const isReadOnlyQuery =
        call.toolName.endsWith("_get") ||
        call.toolName.endsWith("_list") ||
        call.toolName.endsWith("_positions") ||
        call.toolName.endsWith("_status");
      if (isReadOnlyQuery) {
        return Allow();
      }

      if (!ctx.inputs.allowPaperOrders) {
        return Deny(
          "Paper-trading order mutations are disabled for this track (allowPaperOrders=false)."
        );
      }

      if (ctx.state.dataSourcesVerified < 1) {
        return Deny(
          "At least one verified research source is required before submitting paper-trading orders."
        );
      }

      ctx.state.paperOrdersSubmitted += 1;
      return AskHuman(
        `Approve paper-trading action '${call.toolName}' on ${ctx.inputs.tickerOrTheme}?`,
        ["Execute Paper Order", "Reject Order"]
      );
    }

    return Allow();
  },
  schedules: [
    {
      id: "market-open-brief",
      cron: "25 9 * * 1-5",
      timezone: "Asia/Shanghai",
      wakePrompt: (ctx) =>
        `Pre-open check for ${ctx.inputs.tickerOrTheme} vs ${ctx.inputs.benchmark}: scan overnight filings, macro releases, and factor moves.`
    },
    {
      id: "post-close-attribution",
      cron: "30 15 * * 1-5",
      timezone: "Asia/Shanghai",
      wakePrompt:
        "Run post-close Barra factor attribution and archive any material price/volume anomalies."
    }
  ]
};

export default defineTemplate(investmentResearchTemplate);
