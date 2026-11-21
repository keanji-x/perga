// Whether each Planner provider can run on this server right now, and why not (#1817).

import { z } from 'zod';

import { bootstrapDtoSchema,permissionModeOptionDtoSchema,providerStatusDtoSchema,type PermissionModeOptionDto,type ProviderStatusDto } from "../api/perga-v1.js";
import { agentProviderSchema,normalizeWireAgentProvider,type AgentProvider } from "../api/schemas.js";
import type { ApiOperation } from '../api/types.js';
import { NotSentError,writeClassOf,writeFailureOf,type FailureTable,type WriteClass,type WriteText } from './failure-class.js';
import type { ProbeText } from './read-failure.js';

const checkedAtSchema = z.number();
const authenticationNoticeSchema = z.object({
  kind: z.enum(['sign_in_required', 'refresh_error_reported', 'state_unavailable', 'retry_requested']),
  text: z.string(),
  revision: z.string(),
}).nullable();

/**
 * One provider's answer. `reason` is the server's own sentence with its fix, present exactly when the
 * provider is not `ready`; `not_configured` is a backend this server was not started with.
 */
export const providerAvailabilitySchema = z.discriminatedUnion('status', [
  z.object({ provider: agentProviderSchema, status: z.literal('ready'), reason: z.null(), checked_at_ms: checkedAtSchema, authentication_method: z.string().nullable(), authentication_notice: authenticationNoticeSchema }),
  z.object({ provider: agentProviderSchema, status: z.literal('unavailable'), reason: z.string(), checked_at_ms: checkedAtSchema, authentication_method: z.string().nullable(), authentication_notice: authenticationNoticeSchema }),
  z.object({ provider: agentProviderSchema, status: z.literal('not_configured'), reason: z.string(), checked_at_ms: checkedAtSchema, authentication_method: z.string().nullable(), authentication_notice: authenticationNoticeSchema }),
]);

export type ProviderAvailability = z.infer<typeof providerAvailabilitySchema>;

export const agentProvidersSchema = z.array(providerAvailabilitySchema);

export function providerStatusToAvailability(dto: ProviderStatusDto): ProviderAvailability | null {
  const provider = normalizeWireAgentProvider(dto.provider);
  if (provider === null) return null;
  if (dto.available) {
    return {
      provider,
      status: 'ready',
      reason: null,
      checked_at_ms: Date.now(),
      authentication_method: dto.version ?? 'cli',
      authentication_notice: null,
    };
  }
  return {
    provider,
    status: 'unavailable',
    reason: dto.reason ?? `${provider} is unavailable.`,
    checked_at_ms: Date.now(),
    authentication_method: null,
    authentication_notice: null,
  };
}

function mapProvidersToAvailability(providers: readonly ProviderStatusDto[]): ProviderAvailability[] {
  return providers.flatMap((provider) => {
    const mapped = providerStatusToAvailability(provider);
    return mapped === null ? [] : [mapped];
  });
}

/** `GET /api/v1/bootstrap` (or `/api/v1/targets/{targetId}/providers` for non-local targets); `recheck` appends `?refresh=true`. */
export function agentProvidersOperation(recheck: boolean, targetId?: string): ApiOperation<ProviderAvailability[]> {
  if (targetId !== undefined && targetId !== '' && targetId !== 'local') {
    return {
      method: 'GET',
      path: `/api/v1/targets/${encodeURIComponent(targetId)}/providers${recheck ? '?refresh=true' : ''}`,
      responseSchema: z.array(providerStatusDtoSchema).transform(mapProvidersToAvailability),
    };
  }
  return {
    method: 'GET',
    path: recheck ? '/api/v1/bootstrap?scope=providers&refresh=true' : '/api/v1/bootstrap?scope=providers',
    responseSchema: bootstrapDtoSchema.transform((bootstrap) => mapProvidersToAvailability(bootstrap.providers)),
  };
}

/** A failed Recheck, a read-only probe read by `probeFailureText`; the previous answer stays on screen either way. */
export const RECHECK_TEXT: ProbeText = Object.freeze({ answered: 'The recheck failed.', unfinished: 'The recheck did not complete. Try again.' });

/** Availability advice for fresh blank creation; the server owns admission of every shape. */
export function unavailableCreationNote(provider: string): string {
  return `A track can still be created; it runs once ${provider} is back.`;
}

/** `provider`'s entry, or `null` when the answer is not in yet or does not name it. */
export function availabilityOf(
  answers: readonly ProviderAvailability[] | undefined,
  provider: AgentProvider,
): ProviderAvailability | null {
  return answers?.find((answer) => answer.provider === provider) ?? null;
}

export const authenticationRetryResponseSchema = z.object({
  status: z.literal('retry_requested'),
  requested_revision: z.string(),
  recovery_notices: z.array(z.object({ card_id: z.string(), track_id: z.string(), title: z.string(), text: z.string() })),
});
export type AuthenticationRetryResponse = z.infer<typeof authenticationRetryResponseSchema>;

/** Definite refusals precede the durable rearm. A 503 may follow its commit, so it is unknown. */
export const AUTHENTICATION_RETRY_FAILURES: FailureTable<WriteClass> = Object.freeze({
  rules: Object.freeze([Object.freeze({ status: Object.freeze([400, 403, 409]), is: 'refused' as const })]),
  unauthorized: 'refused', otherwise: 'unknown',
});
export const AUTHENTICATION_RETRY_TEXT: WriteText = Object.freeze({
  refused: 'The retry was not allowed. Read the current provider status before trying again.',
  unknown: 'The retry could not be confirmed. Read the current provider status before trying again.',
});
export function authenticationRetryFailureText(error: unknown): string {
  if (writeFailureOf(error) instanceof NotSentError) return 'Not sent. Reconnect before retrying queued messages.';
  return writeClassOf(error, AUTHENTICATION_RETRY_FAILURES) === 'refused'
    ? AUTHENTICATION_RETRY_TEXT.refused : AUTHENTICATION_RETRY_TEXT.unknown;
}

/** Backend-owned managed conversation capabilities; availability is the same cached check. */
export const managedProviderAvailabilitySchema = z.object({
  accepts_unavailable_blank_track: z.boolean(),
  availability: providerAvailabilitySchema,
  permission_modes: z.array(permissionModeOptionDtoSchema).optional(),
  capabilities: z.object({
    managed_conversation: z.boolean(),
    steer: z.boolean(),
    images: z.boolean(),
    manual_compact: z.boolean(),
    history_replacement: z.boolean(),
    requires_ready_availability: z.boolean(),
  }),
});
export type ManagedProviderAvailability = z.infer<typeof managedProviderAvailabilitySchema>;

function mapProvidersToManaged(providers: readonly ProviderStatusDto[]): ManagedProviderAvailability[] {
  return providers.flatMap((provider) => {
    const availability = providerStatusToAvailability(provider);
    if (availability === null) return [];
    return [{
      accepts_unavailable_blank_track: true,
      availability,
      permission_modes: provider.permissionModes,
      capabilities: {
        managed_conversation: true,
        steer: true,
        images: false,
        manual_compact: true,
        history_replacement: false,
        requires_ready_availability: false,
      },
    }];
  });
}

/** Provider menus consume `/api/v1/bootstrap` (or `/api/v1/targets/{targetId}/providers` for non-local targets). */
export function managedProvidersOperation(recheck: boolean, targetId?: string): ApiOperation<ManagedProviderAvailability[]> {
  if (targetId !== undefined && targetId !== '' && targetId !== 'local') {
    return {
      method: 'GET',
      path: `/api/v1/targets/${encodeURIComponent(targetId)}/providers${recheck ? '?refresh=true' : ''}`,
      responseSchema: z.array(providerStatusDtoSchema).transform(mapProvidersToManaged),
    };
  }
  return {
    method: 'GET',
    path: recheck ? '/api/v1/bootstrap?scope=managed&refresh=true' : '/api/v1/bootstrap?scope=managed',
    responseSchema: bootstrapDtoSchema.transform((bootstrap) => mapProvidersToManaged(bootstrap.providers)),
  };
}

export function permissionModesForProvider(
  answers: readonly ManagedProviderAvailability[] | undefined,
  provider: AgentProvider,
): readonly PermissionModeOptionDto[] {
  return answers?.find(({ availability }) => availability.provider === provider)?.permission_modes ?? [];
}

export function defaultPermissionModeFor(options: readonly PermissionModeOptionDto[]): string | null {
  if (options.length === 0) return null;
  return (options.find((option) => option.isDefault) ?? options[0]).id;
}

/** A picker can offer only a declared managed backend. This does not describe Planner create admission. */
export function managedProviderProblem(answers: readonly ManagedProviderAvailability[] | undefined, provider: AgentProvider): string | null {
  const answer = answers?.find(({ availability }) => availability.provider === provider);
  if (answer === undefined) return 'Managed provider capabilities are unavailable. Reconnect before sending.';
  return !answer.capabilities.requires_ready_availability || answer.availability.status === 'ready'
    ? null : answer.availability.reason;
}
