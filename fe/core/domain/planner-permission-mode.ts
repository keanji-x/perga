// A session's permission mode: whether its harness stays in its sandbox, may pause a turn to ask
// the person before acting outside it, or runs with no sandbox at all. Applies from the next turn.

import { z } from 'zod';
import { agentSessionDtoSchema } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';
import type { FailureTable, WriteFailure, WriteText } from './failure-class.js';

export type PlannerPermissionMode = string;
export type SetPlannerPermissionModeResponse = Readonly<{ card_id: string; permission_mode: string }>;

/** Provider-declared mode ID; `null` on the run read means no mode is set / task-bound session. */
export const plannerPermissionModeSchema: z.ZodType<PlannerPermissionMode> = z.string().min(1);

/** Store `mode` on the session via `/api/v1/sessions/{sessionId}/permission-mode`. */
export function setPlannerPermissionModeOperation(
  sessionId: string, mode: PlannerPermissionMode,
): ApiOperation<SetPlannerPermissionModeResponse> {
  return {
    method: 'PUT',
    path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/permission-mode`,
    body: { permission_mode: mode },
    responseSchema: z.union([
      agentSessionDtoSchema.transform((session): SetPlannerPermissionModeResponse => ({
        card_id: session.id,
        permission_mode: session.permissionMode ?? mode,
      })),
      z.object({ card_id: z.string(), permission_mode: plannerPermissionModeSchema }),
    ]),
  };
}

/**
 * What a failed mode change says. 400, 403, 404 and 422 are answered before anything is stored;
 * a 5xx, a lost or an unreadable answer may follow a stored one.
 */
export const PLANNER_PERMISSION_MODE_FAILURES: FailureTable<WriteFailure> = Object.freeze({
  rules: Object.freeze([Object.freeze({ status: Object.freeze([400, 403, 404, 422]), is: 'refused' as const })]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

export const PERMISSION_MODE_CHANGE_TEXT: WriteText = Object.freeze({
  refused: 'The approval setting was not changed.', unknown: 'The approval setting change is unconfirmed.',
});
