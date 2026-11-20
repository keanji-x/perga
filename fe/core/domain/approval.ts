// Execution-scoped live approvals: provider tool permissions and policy-guard pauses.
// Distinct from durable Track Asks (`ask.ts`): an approval is owned by a specific session
// and execution, carries structured option IDs, and settles through `/approvals/{id}/decision`.

import { z } from 'zod';
import { approvalDtoSchema, type ApprovalChoiceDto, type ApprovalDto } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';
import { writeClassOf, writeFailureText, type FailureTable, type WriteClass, type WriteText } from './failure-class.js';

export type PendingApproval = ApprovalDto;
export type ApprovalChoice = ApprovalChoiceDto;

export const NO_PENDING_APPROVALS: readonly PendingApproval[] = Object.freeze([]);

/**
 * Authoritative session scoping for live approvals.
 * Worker sessions read their execution-scoped `workerApprovals` when present, falling back to
 * the Track snapshot filtered by `sessionId`; Lead and Subagent sessions read strictly their own
 * `sessionId` from the Track snapshot so approvals never leak across sessions.
 */
export function approvalsForSession(
  trackApprovals: readonly PendingApproval[] | undefined,
  sessionId: string | null,
  workerApprovals?: readonly PendingApproval[] | null,
): readonly PendingApproval[] {
  if (sessionId === null) return NO_PENDING_APPROVALS;
  if (workerApprovals != null) {
    return workerApprovals.length === 0 ? NO_PENDING_APPROVALS : workerApprovals;
  }
  if (trackApprovals === undefined || trackApprovals.length === 0) return NO_PENDING_APPROVALS;
  const owned = trackApprovals.filter((approval) => approval.sessionId === sessionId);
  return owned.length === 0 ? NO_PENDING_APPROVALS : owned;
}

/** Submit one structured option decision for an active approval. */
export function decideApprovalOperation(
  trackId: string,
  executionId: string,
  approvalId: string,
  optionId: string,
): ApiOperation<undefined> {
  return {
    method: 'POST',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/executions/${encodeURIComponent(executionId)}/approvals/${encodeURIComponent(approvalId)}/decision`,
    body: { optionId },
    responseSchema: z.union([approvalDtoSchema.transform(() => undefined), z.undefined()]),
  };
}

export const APPROVAL_DECISION_FAILURES: FailureTable<WriteClass> = Object.freeze({
  rules: Object.freeze([
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
