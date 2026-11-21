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

export interface PergaTemplateDefinition<TState = Record<string, any>, TInputs = Record<string, any>> {
  id: string;
  title: string;
  description?: string;
  /** 1 uses original prefixed tool names and 2 uses canonical names.
   * Catalog files and old snapshots default to 1, while new inline sources default to 2. */
  toolNameVersion?: 1 | 2;
  /** Catalog base; snapshots freeze its complete source. Child keyed declarations take precedence. */
  extends?: string;
  /** Child hooks replace the inherited slot; omitted hooks inherit the defining module's names.
   * Version 2 uses unprefixed tool names, e.g. type "tool.pr_merge", mcpServer "vcs", toolName "pr_merge". */
  before?: (action: { type: string; role?: string; access?: string; provider?: string; model?: string; arguments?: Record<string, any>; [key: string]: any }, ctx: TemplateContext<TState, TInputs>) => GuardDecision;
  /** Events include schedule.fired ({scheduleId, sourceKind, prompt}), task.settled, report.commit,
   * turn.after, and tool.<name> using the same tool name as before. */
  after?: (event: { type: string; [key: string]: any }, ctx: TemplateContext<TState, TInputs>) => string | null | void;
  plugins?: string[];
  inputs?: Record<string, { type: "string" | "number" | "boolean" | "enum"; values?: string[]; default?: any; optional?: boolean; label?: string }>;
  initialState?: (inputs: TInputs) => TState;
  headerView?: (ctx: TemplateContext<TState, TInputs>) => TemplateHeaderView;
  workspace?: {
    driver?: string;
    lowerCacheDirs?: string[];
    onCreateCmd?: string;
    onCloseCmd?: string;
  };
  report?: {
    maxProseChars?: number;
    sections?: SectionSpec[];
  };
  tasks?: {
    dispatch?: { section?: string; access?: "read_only" | "read_write"; gateInput?: string };
    /** First matching defaults fill omitted fields. enforce denies explicit conflicts.
     * provider/model support ${inputs.key}, including input schema defaults. */
    routing?: Array<{ when: { role?: string; access?: "read_only" | "read_write" }; provider?: string; model?: string; enforce?: boolean }>;
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

declare global {
  function Allow(): GuardDecision;
  function Deny(reason: string): GuardDecision;
  function AskHuman(title: string, labels?: { approve?: string; deny?: string }): GuardDecision;
  function defineTemplate<TState = Record<string, any>, TInputs = Record<string, any>>(
    def: PergaTemplateDefinition<TState, TInputs>
  ): PergaTemplateDefinition<TState, TInputs>;
}
