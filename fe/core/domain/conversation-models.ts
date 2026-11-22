import { z } from 'zod';
import type { AgentProvider } from '../api/generated/wire.js';
import { agentSessionDtoSchema, bootstrapDtoSchema, type AgentSessionDto } from '../api/perga-v1.js';
import { normalizeWireAgentProvider } from '../api/schemas.js';
import type { ApiOperation } from '../api/types.js';
import type { ModelSelection } from './conversation.js';
import { type FailureTable, type WriteFailure } from './failure-class.js';

const reasoningEffortOptionSchema = z.object({
  reasoning_effort: z.string(),
  /* codex's own words for its own setting, shown verbatim; `null` for a Claude effort level, which the CLI declares without one. */
  description: z.string().nullable(),
});

const catalogModelSchema = z.object({
  /* A React key and nothing else — the value that travels to the server is `model`. */
  id: z.string(),
  model: z.string(),
  /* The model this entry runs where the provider says (the Claude CLI's `resolvedModel`); `null` for codex. */
  resolved_model: z.string().nullable(),
  display_name: z.string(),
  description: z.string(),
  /* Which entry codex's own picker highlights, NOT what this installation follows (that is `default` below). */
  is_default: z.boolean(),
  /* A Claude entry's are the CLI's own `supportedEffortLevels`: empty when it declares none. */
  supported_reasoning_efforts: z.array(reasoningEffortOptionSchema),
  /* `null` exactly when the provider declares no default effort (every Claude entry, #1822). */
  default_reasoning_effort: z.string().nullable(),
});

/**
 * `GET /api/models`. `source` and `default_source` are separate answers: otherwise an empty live
 * catalog would read like one the server could not produce. A Claude Planner's catalog is the Claude
 * CLI's own list (#1822), `unavailable` while Claude is not ready; its CLI `default` entry is not a row
 * but the `default` here (`claude_cli`): what it resolves to, and the efforts the `null` selection takes.
 */
export const modelCatalogSchema = z.object({
  models: z.array(catalogModelSchema),
  default: z.object({
    model: z.string().nullable(),
    reasoning_effort: z.string().nullable(),
    /* `null` for codex, whose default's efforts are those of the entry `model` names. */
    supported_reasoning_efforts: z.array(reasoningEffortOptionSchema).nullable(),
  }),
  default_source: z.enum(['config_read', 'config_toml', 'claude_cli', 'acp_session', 'unknown']),
  source: z.enum(['live', 'unavailable']),
  fetched_at_ms: z.number().nullable(),
});

export type ModelCatalog = z.infer<typeof modelCatalogSchema>;

/**
 * Whose catalog: an existing card's, or one for a conversation about to be created on `provider`.
 */
export type ModelCatalogScope =
  | Readonly<{ kind: 'card'; cardId: string; provider: AgentProvider }>
  | Readonly<{ kind: 'provider'; provider: AgentProvider; targetId?: string }>;

function providerStatusesToModelCatalog(
  providers: readonly z.infer<typeof bootstrapDtoSchema>['providers'][number][],
  provider: AgentProvider,
): ModelCatalog {
  const providerStatus = providers.find((p) => normalizeWireAgentProvider(p.provider) === provider);
  const richCatalog = providerStatus?.modelCatalog ?? [];
  if (richCatalog.length > 0) {
    const models = richCatalog.map((entry) => ({
      id: entry.model,
      model: entry.model,
      resolved_model: null,
      display_name: entry.displayName,
      description: entry.description,
      is_default: entry.isDefault,
      supported_reasoning_efforts: entry.supportedReasoningEfforts.map((effort) => ({
        reasoning_effort: effort.reasoningEffort,
        description: effort.description,
      })),
      default_reasoning_effort: entry.defaultReasoningEffort,
    }));
    const defaultEntry = models.find((entry) => entry.is_default);
    return {
      models,
      default: {
        model: defaultEntry?.model ?? null,
        reasoning_effort: defaultEntry?.default_reasoning_effort ?? null,
        supported_reasoning_efforts: defaultEntry?.supported_reasoning_efforts ?? null,
      },
      default_source: defaultEntry ? 'config_read' : 'unknown',
      source: providerStatus?.available ? 'live' : 'unavailable',
      fetched_at_ms: Date.now(),
    };
  }
  const providerModels = providerStatus?.models ?? [];
  const unique = [...new Set(providerModels)];
  return {
    models: unique.map((model, index) => ({
      id: model,
      model,
      resolved_model: null,
      display_name: model,
      description: '',
      is_default: index === 0,
      supported_reasoning_efforts: [],
      default_reasoning_effort: null,
    })),
    default: {
      model: unique[0] ?? null,
      reasoning_effort: null,
      supported_reasoning_efforts: null,
    },
    default_source: 'unknown',
    source: providerStatus?.available ? 'live' : 'unavailable',
    fetched_at_ms: Date.now(),
  };
}

/** Derives the conversation provider's catalog and availability from `/api/v1/bootstrap` or `/api/v1/targets/{targetId}/providers`. */
export function modelCatalogOperation(
  scope: ModelCatalogScope, options?: Readonly<{ provider?: AgentProvider; refresh?: boolean }>,
): ApiOperation<ModelCatalog> {
  if (scope.kind === 'provider' && scope.targetId !== undefined && scope.targetId !== '' && scope.targetId !== 'local') {
    const refreshQuery = options?.refresh === true ? '?refresh=true' : '';
    return {
      method: 'GET',
      path: `/api/v1/targets/${encodeURIComponent(scope.targetId)}/providers${refreshQuery}`,
      responseSchema: bootstrapDtoSchema.shape.providers.transform((providers): ModelCatalog =>
        providerStatusesToModelCatalog(providers, scope.provider)),
    };
  }
  const refreshSuffix = options?.refresh === true ? '&refresh=true' : '';
  return {
    method: 'GET',
    path: scope.kind === 'card'
      ? `/api/v1/bootstrap?card_id=${encodeURIComponent(scope.cardId)}${refreshSuffix}`
      : `/api/v1/bootstrap?provider=${scope.provider}${refreshSuffix}`,
    responseSchema: z.union([
      bootstrapDtoSchema.transform((bootstrap): ModelCatalog =>
        providerStatusesToModelCatalog(bootstrap.providers, scope.provider)),
      modelCatalogSchema,
    ]),
  };
}

export type ModelSelectionResult = AgentSessionDto;

/**
 * Run writes one at a time, keeping only the LATEST waiting intent while one is in flight;
 * a rejected write hands the queue on rather than stranding it. Orders one client's own
 * writes only.
 */
export function createSerialWriter<TArgs, TResult>(
  write: (args: TArgs) => Promise<TResult>,
): (args: TArgs) => Promise<TResult> {
  let inFlight: Promise<TResult> | null = null;
  let queued: TArgs | null = null;
  let queuedIsSet = false;

  const drain = async (args: TArgs): Promise<TResult> => {
    let current = args;
    for (;;) {
      let result: TResult;
      try {
        result = await write(current);
      } catch (error) {
        /* Nothing superseded this one, so its failure is the chain's answer. */
        if (!queuedIsSet) throw error;
        current = takeQueued();
        continue;
      }
      if (!queuedIsSet) return result;
      current = takeQueued();
    }
  };

  const takeQueued = (): TArgs => {
    const next = queued as TArgs;
    queuedIsSet = false;
    queued = null;
    return next;
  };

  return (args: TArgs): Promise<TResult> => {
    if (inFlight === null) {
      const run = drain(args).finally(() => { inFlight = null; });
      inFlight = run;
      return run;
    }
    /* Supersede rather than append: an intent nobody can still see the effect of is not worth a round trip. */
    queued = args;
    queuedIsSet = true;
    return inFlight;
  };
}

export function setPlannerModelOperation(cardId: string, selection: ModelSelection): ApiOperation<ModelSelectionResult> {
  return {
    method: 'PATCH',
    path: `/api/v1/sessions/${encodeURIComponent(cardId)}`,
    body: { model: selection.model, reasoningEffort: selection.reasoning_effort },
    responseSchema: agentSessionDtoSchema,
  };
}

/**
 * What a failed `PATCH /api/v1/sessions/{sessionId}` says. Each listed status is answered before anything is stored:
 * 400, 403, 404 and 422. A 5xx, a lost or an unreadable answer may follow a stored one.
 */
export const PLANNER_MODEL_FAILURES: FailureTable<WriteFailure> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze([409]), code: 'precondition_failed', is: 'refused' as const }),
    Object.freeze({ status: Object.freeze([400, 403, 404, 422]), is: 'refused' as const }),
  ]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

/** What a failed model change says (`refusalText`): not changed, or, when it may have been stored, unconfirmed. */
export const MODEL_CHANGE_TEXT = Object.freeze({ refused: 'The model was not changed.', unknown: 'The model change is unconfirmed.' });
