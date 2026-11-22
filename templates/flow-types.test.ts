import type {
  BuiltinListView, BuiltinTableView, FlowAction, FlowWhen,
  HtmlWidgetFromHostMessage, HtmlWidgetToHostMessage, PergaTemplateSetupResult, TemplateBeforeAction,
} from "./perga-template";

// @ts-expect-error after requires an array of dependencies
const scalarAfter: FlowWhen = { after: "build" };
// @ts-expect-error the dependency array must contain at least one entry
const emptyAfter: FlowWhen = { after: [] };
// @ts-expect-error a node cannot have both an after and a cron trigger
const mixedTriggers: FlowWhen = { after: ["build"], cron: "0 9 * * *" };
// @ts-expect-error run accepts exactly one of cmd and prompt
const mixedRun: FlowAction = { kind: "run", cmd: "verify", prompt: "Review" };
// @ts-expect-error watch accepts exactly one of cmd and prChecks
const mixedWatch: FlowAction = { kind: "watch", cmd: "verify", prChecks: 1 };
const command = { cmd: "build" };
// @ts-expect-error a spread cannot add cmd to wake
const wakeWithCommand: FlowAction = { kind: "wake", session: "lead", prompt: "Check", ...command };
const runWithChecks = { kind: "run" as const, cmd: "build", prChecks: 1 };
// @ts-expect-error a run cannot accept a watch probe
const invalidRun: FlowAction = runWithChecks;
const prompt = { prompt: "Check" };
// @ts-expect-error a spread cannot add prompt to watch
const watchWithPrompt: FlowAction = { kind: "watch", prChecks: 1, ...prompt };
const nestedAction = { kind: "wake" as const, session: "lead" as const, prompt: "Check", run: command };
// @ts-expect-error Flow actions use one flat, kind-tagged variant
const invalidNested: FlowAction = nestedAction;

const triggers: FlowWhen[] = [
  {},
  { after: ["build", { node: "verify", on: "attention" }], on: "ok" },
  { cron: "0 9 * * *", tz: "UTC", maxOccurrences: 3 },
  { at: "2027-01-01T09:00:00Z" },
];
const actions: FlowAction[] = [
  { kind: "run", cmd: "verify" },
  { kind: "run", prompt: "Review", session: "new" },
  { kind: "wake", session: "lead", prompt: "Check progress" },
  { kind: "watch", prChecks: 1, repo: "example/project" },
  { kind: "watch", cmd: "verify", interval: "5s" },
];

const before: NonNullable<PergaTemplateSetupResult["before"]> = (action, ctx) => {
  switch (action.type) {
    case "report.commit":
      return action.editorRole === "lead" && action.nextMarkdown !== action.prevMarkdown ? Allow() : Deny("no change");
    case "task.dispatch":
      return action.taskKey && action.access === "read_only" ? Allow() : Deny("requires a gate");
    case "turn.before":
      return action.contextUsageRatio < 1 ? Allow() : Deny("context full");
    default:
      return action.toolName === "report_read" && action.arguments && ctx.session?.isLead ? Allow() : Deny("lead only");
  }
};
const toolAction: TemplateBeforeAction = {
  type: "tool.report_read", mcpServer: "perga", toolName: "report_read", arguments: {}, sessionId: "lead", isLead: true,
};
// @ts-expect-error report.commit requires a previous and next report
const incompleteReportCommit: TemplateBeforeAction = { type: "report.commit", editorRole: "lead", nextMarkdown: "" };

const table: BuiltinTableView = {
  metrics: [{
    label: "Coverage", target: 0.9, direction: "gte", status: "on_track",
    points: [{ date: "2026-01-01", value: 0.8 }, { date: "2026-01-02", value: 0.85, note: null }],
  }],
};
const list: BuiltinListView = {
  title: null,
  items: [{
    id: "review", title: "Review", status: "running", assignKind: "model",
    checks: [{ label: "Unit tests", status: "pass" }],
    expectations: [{ label: "Coverage", expected: "90%", status: "pending" }],
  }],
};
// @ts-expect-error a table requires metrics
const invalidTable: BuiltinTableView = {};
// @ts-expect-error list items use editorial status values
const invalidList: BuiltinListView = { items: [{ id: "a", title: "A", status: "pass" }] };

const template: PergaTemplateSetupResult = {
  before,
  report: { initialMarkdown: "# Report", views: { checks: () => list, coverage: () => table } },
};
// @ts-expect-error report.template and report.initialMarkdown are mutually exclusive
const dualReportTemplate: PergaTemplateSetupResult = { report: { template: "./report.md", initialMarkdown: "# Report" } };

const widgetMessages: HtmlWidgetToHostMessage[] = [
  { type: "perga:ready" },
  { type: "perga:resize", height: 120 },
  { type: "perga:intent", kind: "open_file", path: "report.md" },
  { type: "perga:intent", kind: "open_session", sessionId: "ses_example" },
];
const hostMessages: HtmlWidgetFromHostMessage<BuiltinTableView>[] = [
  { type: "perga:theme", scheme: "light", tokens: { bg: "#fff" } },
  { type: "perga:data", data: table },
];
// @ts-expect-error open_file carries a workspace-relative path
const invalidIntent: HtmlWidgetToHostMessage = { type: "perga:intent", kind: "open_file", sessionId: "lead" };
