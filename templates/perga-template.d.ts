export type GuardDecision =
  | { kind: "allow" }
  | { kind: "deny"; reason: string }
  | { kind: "ask_human"; title: string; options?: string[] };

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
  plugins?: string[];
  inputs?: Record<string, { type: "string" | "number" | "boolean" | "enum"; values?: string[]; default?: any; optional?: boolean; label?: string }>;
  initialState?: (inputs: TInputs) => TState;
  initialMarkdown?: string;
  workspace?: {
    driver?: string;
    lowerCacheDirs?: string[];
    onCreateCmd?: string;
    onCloseCmd?: string;
  };
  report?: {
    maxProseChars?: number;
    sections?: SectionSpec[];
    customValidate?: (ctx: TemplateContext<TState, TInputs>, info: {
      editorRole: string;
      prevMarkdown: string;
      nextMarkdown: string;
      touchedSections: string[];
      nextProseChars: number;
    }) => GuardDecision;
  };
  tasks?: {
    dispatch?: { section?: string; access?: "read_only" | "read_write"; gateInput?: string };
    requireGateForReadWrite?: boolean;
    afterWorkerDoneCmd?: string;
    onSettled?: (ctx: TemplateContext<TState, TInputs>, event: {
      taskKey: string;
      accessMode: "read_only" | "read_write";
      attemptStatus: string;
      workerResult?: any;
      meta: Record<string, any>;
      gateVerdict?: any;
    }) => string | void;
  };
  buildTurnContext?: (ctx: TemplateContext<TState, TInputs>, session: {
    sessionId: string;
    isLead: boolean;
    boundAttemptId?: string;
    contextUsageRatio: number;
  }) => string | void;
  guardToolCall?: (ctx: TemplateContext<TState, TInputs>, call: {
    sessionId: string;
    isLead: boolean;
    boundAttemptId?: string;
    mcpServer: string;
    toolName: string;
    arguments: Record<string, any>;
  }) => GuardDecision;
  schedules?: Array<{
    id: string;
    cron: string;
    timezone?: string;
    wakePrompt: string | ((ctx: TemplateContext<TState, TInputs>) => string);
  }>;
}

declare global {
  function Allow(): GuardDecision;
  function Deny(reason: string): GuardDecision;
  function AskHuman(title: string, options?: string[]): GuardDecision;
  function defineTemplate<TState = Record<string, any>, TInputs = Record<string, any>>(
    def: PergaTemplateDefinition<TState, TInputs>
  ): PergaTemplateDefinition<TState, TInputs>;
}
