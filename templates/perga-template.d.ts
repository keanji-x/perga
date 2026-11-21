/** A pure JSON object may contain the declarative fields of this definition.
 * Function hooks require an ES module: export default defineTemplate({...}).
 * To reapply a legacy template with track_set, pass jsSource or snapshot from template_read unchanged.
 * Use editableSource to read or edit the underlying template. */
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

export interface SectionSpec {
  h1: string;
  omitIfEmpty?: boolean;
  editableBy?: Array<"human" | "lead" | "subagent" | "system">;
}

export interface TemplateContext<TState = Record<string, any>, TInputs = Record<string, any>> {
  trackId: string;
  inputs: TInputs;
  state: TState;
}

export interface TaskRoutingRuleSpec {
  when: { role?: string; access?: "read_only" | "read_write" };
  provider?: string;
  model?: string;
  access?: "read_only" | "read_write";
  enforce?: boolean;
}

export interface CheckpointSpec {
  id: string;
  title: string;
  role?: string;
  access?: "read_only" | "read_write";
  dependsOn?: string[];
  docPath?: string;
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
    boundAttemptId?: string | null;
    status?: string;
  }>;
}

export interface TriggerEventContext<TState = Record<string, any>> {
  data: TState;
  event: Record<string, any>;
  kernel: KernelFactsSnapshot;
}

export interface TriggerSpec<TState = Record<string, any>> {
  id: string;
  label: string;
  on:
    | { kind: "cron"; expr: string; timezone?: string; summary?: string }
    | { kind: "task_settled"; taskKey?: string; role?: string; summary?: string }
    | { kind: "data_change"; path: string; summary?: string }
    | { kind: "schedule_at"; dataPath: string; dateField: string; summary?: string }
    | { kind: "event"; event: string; filter?: Record<string, unknown>; summary?: string }
    | { kind: "manual"; summary?: string };
  when?: (ctx: TriggerEventContext<TState>) => boolean;
  action:
    | { kind: "run_cli"; command: string; writeDataPath?: string; then?: string }
    | {
        kind: "dispatch_worker";
        taskKey?: string;
        bindRef?: string;
        role?: string;
        model?: string;
        access?: "read_only" | "read_write";
        goal?: string | ((ctx: TriggerEventContext<TState>) => string);
      }
    | { kind: "wake_lead"; prompt?: string | ((ctx: TriggerEventContext<TState>) => string) };
}

export interface PergaTemplateSetupResult<TState = Record<string, any>, TInputs = Record<string, any>> {
  plugins?: string[];
  data?: {
    schema?: ((t: SchemaBuilder) => Record<string, SchemaNode>) | SchemaNode;
    initial?: (inputs: TInputs) => TState;
  };
  initialState?: (inputs: TInputs) => TState;
  headerView?: (ctx: TemplateContext<TState, TInputs>) => TemplateHeaderView;
  before?: (action: { type: string; role?: string; access?: string; provider?: string; model?: string; arguments?: Record<string, any>; [key: string]: any }, ctx: TemplateContext<TState, TInputs>) => GuardDecision;
  after?: (event: { type: string; [key: string]: any }, ctx: TemplateContext<TState, TInputs>) => string | null | void;
  checkpoints?: CheckpointSpec[];
  triggers?: TriggerSpec<TState>[];
  workspace?: {
    driver?: string;
    lowerCacheDirs?: string[];
    onCreateCmd?: string;
    onCloseCmd?: string;
  };
  report?: {
    humanReadOnly?: boolean;
    template?: string;
    views?: Record<string, (data: TState, kernel: KernelFactsSnapshot) => unknown>;
    maxProseChars?: number;
    sections?: SectionSpec[];
  };
  tasks?: {
    dispatch?: { section?: string; access?: "read_only" | "read_write"; gateInput?: string };
    /** First matching defaults fill omitted fields. enforce denies explicit conflicts.
     * provider/model support ${inputs.key}, including input schema defaults. */
    routing?: TaskRoutingRuleSpec[];
    requireGateForReadWrite?: boolean;
    afterWorkerDoneCmd?: string;
  };
  turnContext?: (ctx: TemplateContext<TState, TInputs> & { session: {
    sessionId: string;
    isLead: boolean;
    boundAttemptId?: string;
    contextUsageRatio: number;
  } }) => string | void;
  /** Declarative prompts support ${inputs.key}; use after(schedule.fired) for stateful decisions. */
  schedules?: Array<{
    id: string;
    cron: string;
    timezone?: string;
    title?: string;
    prompt: string;
  }>;
}

export interface PergaTemplateDefinition<TState = Record<string, any>, TInputs = Record<string, any>>
  extends PergaTemplateSetupResult<TState, TInputs> {
  id: string;
  title: string;
  description?: string;
  /** 1 uses original prefixed tool names and 2 uses canonical names.
   * Catalog files and old snapshots default to 1, while new inline sources default to 2. */
  toolNameVersion?: 1 | 2;
  /** Catalog base; snapshots freeze its complete source. Child keyed declarations take precedence. */
  extends?: string;
  inputs?: Record<string, { type: "string" | "number" | "boolean" | "enum"; values?: string[]; default?: any; optional?: boolean; label?: string }>;
  setup?: (inputs: TInputs) => PergaTemplateSetupResult<TState, TInputs>;
}

declare global {
  function Allow(): GuardDecision;
  function Deny(reason: string): GuardDecision;
  function AskHuman(title: string, labels?: { approve?: string; deny?: string }): GuardDecision;
  function defineTemplate<TState = Record<string, any>, TInputs = Record<string, any>>(
    def: PergaTemplateDefinition<TState, TInputs>
  ): PergaTemplateDefinition<TState, TInputs>;
}
