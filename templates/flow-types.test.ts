import type {
  BuiltinListView, BuiltinTableView, FlowAction, FlowWhen,
  HtmlWidgetFromHostMessage, HtmlWidgetToHostMessage, PergaTemplateSetupResult, TemplateBeforeAction, TemplateViewFn,
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
// @ts-expect-error repeating watches require a command probe
const repeatedPrCheck: FlowAction = { kind: "watch", prChecks: 1, repeat: true };
// @ts-expect-error shell runs have no model effort
const commandWithEffort: FlowAction = { kind: "run", cmd: "verify", reasoningEffort: "high" };
// @ts-expect-error wake reuses a session's existing configuration
const wakeWithEffort: FlowAction = { kind: "wake", session: "lead", prompt: "Review", reasoningEffort: "high" };
// @ts-expect-error shell runs have no permissionMode
const commandWithPermissionMode: FlowAction = { kind: "run", cmd: "verify", permissionMode: "full" };
// @ts-expect-error wake reuses a session's existing permissionMode
const wakeWithPermissionMode: FlowAction = { kind: "wake", session: "lead", prompt: "Review", permissionMode: "full" };
// @ts-expect-error a Track Lead requires a first instruction
const trackWithoutPrompt: FlowAction = { kind: "track" };
// @ts-expect-error template inputs require a template
const trackWithUnboundInputs: FlowAction = { kind: "track", prompt: "Explore", templateInputs: {} };
// @ts-expect-error template inputs must be an object
const trackWithArrayInputs: FlowAction = { kind: "track", prompt: "Explore", template: "agents-delivery", templateInputs: [] };
const trackWithSession = { kind: "track" as const, prompt: "Explore", session: "new" };
// @ts-expect-error a Track spawn cannot target a conversation
const invalidTrack: FlowAction = trackWithSession;

const triggers: FlowWhen[] = [
  {},
  { after: ["build", { node: "verify", on: "attention" }], on: "ok" },
  { cron: "0 9 * * *", tz: "UTC", maxOccurrences: 3 },
  { at: "2027-01-01T09:00:00Z" },
];
const actions: FlowAction[] = [
  { kind: "run", cmd: "verify" },
  { kind: "run", prompt: "Review", session: "new", reasoningEffort: "high", permissionMode: "full" },
  { kind: "wake", session: "lead", prompt: "Check progress" },
  { kind: "watch", prChecks: 1, repo: "example/project", timeout: "7d" },
  { kind: "watch", cmd: "verify", interval: "5s", timeout: "never", repeat: true },
  { kind: "track", prompt: "Explore" },
  {
    kind: "track", prompt: "Explore", title: "Research", area: "Engineering", template: "agents-delivery",
    templateInputs: { feature: "cache", targets: ["web"] }, cwd: "${inputs.cwd}", workspaceDriver: "vcs-workspace",
    executionTargetId: "local", provider: "codex", model: "gpt-5", reasoningEffort: "high", permissionMode: "full",
  },
];

const before: NonNullable<PergaTemplateSetupResult["before"]> = (action, ctx) => {
  switch (action.type) {
    case "report.commit":
      return action.editorRole === "lead" && action.nextMarkdown !== action.prevMarkdown ? Allow() : Deny("no change");
    case "task.dispatch":
      return action.taskKey && action.access === "read_only" && action.permissionMode !== "full" ? Allow() : Deny("requires a gate");
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
  columns: [{ key: "coverage", label: "Coverage", align: "right" }],
  rows: [{ coverage: { text: "90%", tone: "good", code: "KR1", tag: "CI", check: true } }, { coverage: null }],
};
const list: BuiltinListView = {
  title: null,
  items: [{
    id: "review", title: "Review", subtitle: "Release", badge: "Ready", tone: "good",
    meta: "Review worker", note: "See the unit results", table,
    checks: [{ label: "Unit tests", status: "pass" }],
  }],
};
// @ts-expect-error a table requires columns and rows
const invalidTable: BuiltinTableView = {};
// @ts-expect-error a table cell requires text
const invalidCell: BuiltinTableView = { columns: table.columns, rows: [{ coverage: { check: true } }] };
// @ts-expect-error list items do not accept domain-specific fields
const invalidItem: BuiltinListView = { items: [{ id: "a", title: "A", verdict: "beat" }] };
const useInputs: TemplateViewFn<{ target: string }> = (data, kernel, inputs) => {
  // @ts-expect-error view inputs are read-only
  inputs.target = "new target";
  return { target: inputs.target, data, tasks: kernel.tasks };
};
const existingView: TemplateViewFn = (data, kernel) => ({ data, tasks: kernel.tasks });
// @ts-expect-error list items use editorial status values
const invalidList: BuiltinListView = { items: [{ id: "a", title: "A", status: "pass" }] };

const withInputs: PergaTemplateSetupResult<{ target: string }> = { report: { views: { useInputs } } };

const template: PergaTemplateSetupResult = {
  before,
  report: { initialMarkdown: "# Report", views: { checks: () => list, coverage: () => table, existingView } },
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
