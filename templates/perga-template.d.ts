/** A pure JSON object may contain the declarative fields of this definition.
 * Function hooks require an ES module: export default defineTemplate({...}).
 * Pass jsSource or snapshot from template_read to reapply a template with track_set. */
export type Tone = "neutral" | "accent" | "success" | "warn" | "error";

export interface TemplateHeaderView {
  badges?: Array<{ label: string; tone?: Tone }>;
  notice?: { text: string; tone?: "info" | "warn" | "error" };
  metrics?: Array<{ label: string; value: string; delta?: string; tone?: Tone; sub?: string; spark?: number[] }>;
  rules?: Array<{ label: string; text: string }>;
}

export type GuardDecision =
  | { kind: "allow" }
  | { kind: "deny"; reason: string }
  | { kind: "ask_human"; title: string; approveLabel?: string; denyLabel?: string };

export type TemplateBeforeAction =
  | {
      type: `tool.${string}`;
      mcpServer: string;
      toolName: string;
      arguments: Record<string, any>;
      sessionId: string;
      isLead: boolean;
      boundTaskId?: string | null;
    }
  | { type: "report.commit"; editorRole: "lead" | "subagent"; prevMarkdown: string; nextMarkdown: string }
  | {
      type: "task.dispatch";
      taskKey: string;
      role?: string | null;
      access: "read_only" | "read_write";
      provider?: string | null;
      model?: string | null;
      gate?: unknown;
      acceptance?: string | null;
    }
  | {
      type: "turn.before";
      sessionId: string;
      isLead: boolean;
      boundTaskId?: string | null;
      contextUsageRatio: number;
      context?: string | null;
    };

export interface EditorialCheck {
  label: string;
  status: "pass" | "running" | "fail" | "pending";
  detail?: string | null;
  command?: string | null;
  output?: string | null;
}

export interface EditorialExpectation {
  label: string;
  expected: string;
  actual?: string | null;
  status?: "pending" | "beat" | "inline" | "miss" | null;
}

export interface EditorialMetricPoint {
  date: string;
  value: number;
  note?: string | null;
}

export interface EditorialMetric {
  code?: string | null;
  label: string;
  tag?: string | null;
  current?: string | null;
  status?: "met" | "on_track" | "at_risk" | "pending" | null;
  updatedAt?: string | null;
  evidence?: string | null;
  unit?: string | null;
  target?: number | null;
  direction?: "lte" | "gte" | null;
  targetLabel?: string | null;
  /** When provided, 2–90 points. */
  points?: EditorialMetricPoint[] | null;
}

export interface EditorialListItem {
  id: string;
  title: string;
  status: "done" | "running" | "pending" | "blocked";
  statusLabel?: string | null;
  group?: string | null;
  date?: string | null;
  verdict?: string | null;
  triggerAt?: string | null;
  assignKind?: "model" | "command" | null;
  assign?: string | null;
  summary?: string | null;
  bullets?: string[] | null;
  docLabel?: string | null;
  sessionId?: string | null;
  taskKey?: string | null;
  workerLabel?: string | null;
  model?: string | null;
  blockedReason?: string | null;
  expectations?: EditorialExpectation[] | null;
  checks?: EditorialCheck[] | null;
}

/** View result for `builtin:table`. At most 12 metrics. */
export interface BuiltinTableView {
  metrics: EditorialMetric[];
}

/** View result for `builtin:list`. At most 100 items and groups, and 1–12 metrics when supplied. */
export interface BuiltinListView {
  items: EditorialListItem[];
  title?: string | null;
  summary?: string | null;
  groups?: string[] | null;
  metrics?: EditorialMetric[] | null;
}

/** Messages sent by a sandboxed `./widgets/*.html` iframe to its parent with postMessage.
 * Send ready to request the current theme and data. Resize height is in CSS pixels.
 * Intent paths are workspace-relative. */
export type HtmlWidgetToHostMessage =
  | { type: "perga:ready" }
  | { type: "perga:resize"; height: number }
  | { type: "perga:intent"; kind: "open_file"; path: string }
  | { type: "perga:intent"; kind: "open_session"; sessionId: string };

/** Messages sent by the host to the widget iframe. Data is the selected report view result. */
export type HtmlWidgetFromHostMessage<TData = unknown> =
  | { type: "perga:theme"; scheme: "light" | "dark"; tokens: Readonly<Record<string, string>> }
  | { type: "perga:data"; data: TData };

export interface TemplateContext<TInputs = Record<string, any>, TData = Record<string, any>> {
  readonly trackId: string;
  readonly inputs: Readonly<TInputs>;
  readonly data: Readonly<TData>;
}

export interface TemplateSessionContext {
  readonly sessionId: string;
  readonly isLead: boolean;
  readonly boundTaskId?: string;
  readonly contextUsageRatio: number;
}

export interface TaskRoutingRuleSpec {
  when: { role?: string; access?: "read_only" | "read_write" };
  provider?: string;
  model?: string;
  access?: "read_only" | "read_write";
  enforce?: boolean;
}

export interface SchemaNode {
  readonly __pergaSchema: true;
  optional(): SchemaNode;
  /** Supply a JSON value for a missing field. Explicit values, including null on optional fields, are preserved. */
  default(value: unknown): SchemaNode;
}

export interface SchemaBuilder {
  id(): SchemaNode;
  string(): SchemaNode;
  markdown(): SchemaNode;
  number(): SchemaNode;
  boolean(): SchemaNode;
  isoDate(): SchemaNode;
  enum(values: readonly string[]): SchemaNode;
  ref(kind: "task" | "file"): SchemaNode;
  array(item: SchemaNode): SchemaNode;
  object(shape: Record<string, SchemaNode>): SchemaNode;
}

export interface KernelTaskFact {
  id: string;
  taskKey: string;
  status: "pending" | "running" | "done" | "blocked";
  provider?: string | null;
  model?: string | null;
  sessionId?: string | null;
  gates?: Array<{
    label: string;
    status: "pass" | "fail" | "running" | "pending";
    detail?: string | null;
    command?: string | null;
    output?: string | null;
  }>;
  updatedAtMs?: number;
}

export interface KernelFactsSnapshot {
  trackId?: string;
  tasks: Record<string, KernelTaskFact>;
  sessions?: Record<string, {
    id: string;
    isLead: boolean;
    provider?: string | null;
    model?: string | null;
    status?: string;
  }>;
}

export type FlowOutcomeCondition = "ok" | "attention" | "blocked" | "any";
export type FlowAfterRef = string | { node: string; on?: FlowOutcomeCondition };

export type FlowWhen =
  | { after: readonly [FlowAfterRef, ...FlowAfterRef[]]; on?: FlowOutcomeCondition;
      cron?: never; tz?: never; maxOccurrences?: never; at?: never }
  | { cron: string; tz?: string; maxOccurrences?: number; after?: never; on?: never; at?: never }
  | { at: string; after?: never; on?: never; cron?: never; tz?: never; maxOccurrences?: never }
  | Record<string, never>;

export type FlowAction =
  | ({ kind: "run"; access?: "read_only" | "read_write"; gate?: string; acceptance?: string;
      run?: never; wake?: never; watch?: never; reuseSession?: never;
      prChecks?: never; repo?: never; headSha?: never; interval?: never; timeout?: never } & (
      | { prompt: string; cmd?: never; session?: "new" | `ses_${string}`; role?: string; provider?: string; model?: string }
      | { cmd: string; prompt?: never; session?: never; role?: never; provider?: never; model?: never }
    ))
  | { kind: "wake"; session: "lead" | `ses_${string}`; prompt: string;
      run?: never; wake?: never; watch?: never; cmd?: never; reuseSession?: never;
      role?: never; access?: never; provider?: never; model?: never; gate?: never; acceptance?: never;
      prChecks?: never; repo?: never; headSha?: never; interval?: never; timeout?: never }
  | ({ kind: "watch"; interval?: string; timeout?: string;
      run?: never; wake?: never; watch?: never; prompt?: never; session?: never; reuseSession?: never;
      role?: never; access?: never; provider?: never; model?: never; gate?: never; acceptance?: never } & (
      | { prChecks: number; cmd?: never; repo?: string; headSha?: string }
      | { cmd: string; prChecks?: never; repo?: never; headSha?: never }
    ));

export interface FlowNode {
  id: string;
  title?: string;
  /** Seeded nodes receive template provenance. */
  source?: "agent" | "user" | "template";
  when?: FlowWhen;
  do: FlowAction;
  canceled?: boolean;
}

export interface PergaTemplateSetupResult<TInputs = Record<string, any>, TData = Record<string, any>> {
  /** Core declarations resolved for the Track's inputs. */
  /** Initial flow.v1 nodes with runtime interpolation in supported text fields. */
  flow?: FlowNode[];
  data?: {
    schema?: ((t: SchemaBuilder) => Record<string, SchemaNode>) | SchemaNode;
    initial?: TData | ((inputs: TInputs) => TData);
  };
  report?: ({
    /** Package-relative Markdown seeded on Track creation. Supports ${inputs.key}. Supply either this or initialMarkdown. */
    template?: string;
    initialMarkdown?: never;
  } | {
    template?: never;
    /** Inline Markdown seeded on Track creation. Supports ${inputs.key}. Supply either this or template. */
    initialMarkdown?: string;
  }) & {
    views?: Record<string, (data: TData, kernel: KernelFactsSnapshot) => unknown>;
  };
  tasks?: {
    /** First matching defaults fill omitted fields. enforce denies explicit conflicts.
     * provider/model support ${inputs.key}, including input schema defaults. */
    routing?: TaskRoutingRuleSpec[];
    requireGateForReadWrite?: boolean;
    afterWorkerDoneCmd?: string;
  };
  turnContext?: (ctx: TemplateContext<TInputs, TData> & { readonly session: TemplateSessionContext }) => string | void;

  /** Advanced policy and workspace hooks. */
  headerView?: (ctx: TemplateContext<TInputs, TData>) => TemplateHeaderView;
  before?: (action: TemplateBeforeAction,
    ctx: TemplateContext<TInputs, TData> & { readonly session?: TemplateSessionContext }) => GuardDecision;
  workspace?: {
    driver?: string;
    lowerCacheDirs?: string[];
    onCreateCmd?: string;
    onCloseCmd?: string;
  };
  plugins?: string[];
}

export interface PergaTemplateDefinition<TInputs = Record<string, any>, TData = Record<string, any>>
  extends PergaTemplateSetupResult<TInputs, TData> {
  id: string;
  title: string;
  description?: string;
  /** Input declarations and input-dependent configuration. */
  inputs?: Record<string, { type: "string" | "number" | "boolean" | "enum"; values?: string[]; default?: any; optional?: boolean; label?: string }>;
  setup?: (inputs: TInputs) => PergaTemplateSetupResult<TInputs, TData>;

  /** Catalog base; snapshots freeze its complete source. Child keyed declarations take precedence. */
  extends?: string;
}

declare global {
  function Allow(): GuardDecision;
  function Deny(reason: string): GuardDecision;
  function AskHuman(title: string, labels?: { approve?: string; deny?: string }): GuardDecision;
  function defineTemplate<TInputs = Record<string, any>, TData = Record<string, any>>(
    def: PergaTemplateDefinition<TInputs, TData>
  ): PergaTemplateDefinition<TInputs, TData>;
}
