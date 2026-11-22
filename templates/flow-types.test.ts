import type { FlowAction, FlowWhen } from "./perga-template";

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
