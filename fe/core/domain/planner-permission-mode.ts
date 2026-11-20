// A session's permission mode: whether its harness stays in its sandbox, may pause a turn to ask
// the person before acting outside it, or runs with no sandbox at all. Applies from the next turn.
// Single authoritative resolver shared by new-track, new-conversation draft, and existing-session UIs.

import { z } from 'zod';
import { agentSessionDtoSchema, type AgentSessionDto, type PermissionModeOptionDto } from '../api/perga-v1.js';
import type { AgentProvider } from '../api/generated/wire.js';
import type { ApiOperation } from '../api/types.js';
import { defaultPermissionModeFor } from './agent-providers.js';
import type { FailureTable, WriteFailure, WriteText } from './failure-class.js';

export type PlannerPermissionMode = string;
export type SetPlannerPermissionModeResponse = AgentSessionDto;

/** Provider-declared mode ID; `null` on the run read means no mode is set / task-bound session. */
export const plannerPermissionModeSchema: z.ZodType<PlannerPermissionMode> = z.string().min(1);

export type ResolvedPermissionControl =
  | Readonly<{ visible: false }>
  | Readonly<{
      visible: true;
      mode: string | null;
      options: readonly PermissionModeOptionDto[];
      isDisabled: boolean;
    }>;

const HIDDEN_PERMISSION_CONTROL: ResolvedPermissionControl = Object.freeze({ visible: false });

/**
 * Drafts use a declared default until the user selects an option. Existing sessions
 * retain the stored mode even when the current catalogue cannot describe it yet.
 */
export function resolvePermissionControl(params: Readonly<{
  source: 'draft' | 'session';
  storedMode: string | null | undefined;
  providerModes: readonly PermissionModeOptionDto[] | undefined;
  metadataLoaded: boolean;
  isDisabled: boolean;
}>): ResolvedPermissionControl {
  const stored = params.storedMode ?? null;
  const declared = params.providerModes ?? [];
  if (declared.length > 0) {
    const mode = params.source === 'session' || declared.some((option) => option.id === stored)
      ? stored
      : defaultPermissionModeFor(declared)!;
    return {
      visible: true,
      mode,
      options: declared,
      isDisabled: params.isDisabled,
    };
  }

  if (stored !== null && (params.source === 'session' || !params.metadataLoaded)) {
    return {
      visible: true,
      mode: stored,
      options: declared,
      isDisabled: true,
    };
  }

  return HIDDEN_PERMISSION_CONTROL;
}

/** Switching providers clears any mode ID from the previous provider so the new provider's default applies. */
export function permissionModeAfterProviderChange(
  previousProvider: AgentProvider,
  nextProvider: AgentProvider,
  currentMode: string | null | undefined,
): string | null {
  return previousProvider === nextProvider ? (currentMode ?? null) : null;
}

/** Store `mode` on the session via `/api/v1/sessions/{sessionId}/permission-mode`. */
export function setPlannerPermissionModeOperation(
  sessionId: string, mode: PlannerPermissionMode,
): ApiOperation<SetPlannerPermissionModeResponse> {
  return {
    method: 'PUT',
    path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/permission-mode`,
    body: { permissionMode: mode },
    responseSchema: agentSessionDtoSchema,
  };
}

/**
 * What a failed mode change says. Discovery preconditions and 400, 403, 404 and 422
 * are answered before anything is stored;
 * a 5xx, a lost or an unreadable answer may follow a stored one.
 */
export const PLANNER_PERMISSION_MODE_FAILURES: FailureTable<WriteFailure> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze([409]), code: 'precondition_failed', is: 'refused' as const }),
    Object.freeze({ status: Object.freeze([400, 403, 404, 422]), is: 'refused' as const }),
  ]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

export const PERMISSION_MODE_CHANGE_TEXT: WriteText = Object.freeze({
  refused: 'The approval setting was not changed.', unknown: 'The approval setting change is unconfirmed.',
});
