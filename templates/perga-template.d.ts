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
  report?: {
    /** Package-relative Markdown seeded on Track creation. Supports ${inputs.key}. */
    template?: string;
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
  before?: (action: { type: string; role?: string; access?: string; provider?: string; model?: string; arguments?: Record<string, any>; [key: string]: any },
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
