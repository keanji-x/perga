// Execution-scoped live approvals: provider tool permissions and policy-guard pauses.
// Distinct from durable Track Asks (`ask.ts`): an approval is owned by a specific session
// and execution, carries structured option IDs, and settles through `/sessions/{sessionId}/interactions/{interactionId}/respond`.

import { sessionInteractionResultSchema, type ApprovalChoiceDto, type ApprovalDto } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';
import { writeClassOf, writeFailureText, type FailureTable, type WriteClass, type WriteText } from './failure-class.js';

export type PendingApproval = Pick<ApprovalDto, 'id' | 'executionId' | 'sessionId' | 'turnId' | 'title' | 'options' | 'createdAtMs' | 'toolName' | 'arguments'>;
export type ApprovalChoice = ApprovalChoiceDto;

export const NO_PENDING_APPROVALS: readonly PendingApproval[] = Object.freeze([]);

/** Pending interactions from a session read are authoritative; aggregate notifications are session-scoped. */
export function approvalsForSession(
  trackApprovals: readonly PendingApproval[] | undefined,
  sessionId: string | null,
  sessionApprovals?: readonly PendingApproval[] | null,
): readonly PendingApproval[] {
  if (sessionId === null) return NO_PENDING_APPROVALS;
  if (sessionApprovals != null) {
    return sessionApprovals.length === 0 ? NO_PENDING_APPROVALS : sessionApprovals;
  }
  if (trackApprovals === undefined || trackApprovals.length === 0) return NO_PENDING_APPROVALS;
  const owned = trackApprovals.filter((approval) => approval.sessionId === sessionId);
  return owned.length === 0 ? NO_PENDING_APPROVALS : owned;
}

/** Submit one structured option decision for an active approval. */
export function decideApprovalOperation(
  sessionId: string,
  executionId: string,
  approvalId: string,
  optionId: string,
): ApiOperation<undefined> {
  return {
    method: 'POST',
    path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/interactions/${encodeURIComponent(approvalId)}/respond`,
    body: { executionId, optionId },
    responseSchema: sessionInteractionResultSchema.transform(() => undefined),
  };
}

export const APPROVAL_DECISION_FAILURES: FailureTable<WriteClass> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze([409]), code: 'precondition_failed', is: 'refused' as const }),
    Object.freeze({ status: Object.freeze([409]), is: 'done' as const }),
    Object.freeze({ status: Object.freeze([400, 403, 404, 413, 415, 422]), is: 'refused' as const }),
  ]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

export const APPROVAL_DECISION_TEXT: WriteText = Object.freeze({
  refused: 'Your decision was not sent.',
  unknown: 'Sending your decision is unconfirmed.',
});

export const APPROVAL_GONE_TEXT = 'This request is no longer pending.';

export function approvalDecisionFailureText(error: unknown): string | null {
  if (writeClassOf(error, APPROVAL_DECISION_FAILURES) === 'done') return APPROVAL_GONE_TEXT;
  return writeFailureText(APPROVAL_DECISION_FAILURES, APPROVAL_DECISION_TEXT)(error);
}
