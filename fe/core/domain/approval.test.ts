import { describe, expect, it } from 'vitest';
import {
  APPROVAL_GONE_TEXT, approvalDecisionFailureText, approvalsForSession, decideApprovalOperation,
  NO_PENDING_APPROVALS, type PendingApproval,
} from './approval.js';
import { ApiError } from './failure-class.js';

const LEAD_APPROVAL: PendingApproval = {
  id: 'apr-lead', executionId: 'turn-1', sessionId: 'ses-lead', turnId: 'turn-1',
  title: 'Approve tool shell?',
  options: [
    { id: 'allow_once', label: 'Allow once', isApproval: true },
    { id: 'reject_once', label: 'Reject once', isApproval: false },
  ],
  createdAtMs: 1,
};

const SUBAGENT_APPROVAL: PendingApproval = {
  id: 'apr-sub', executionId: 'turn-2', sessionId: 'ses-sub', turnId: 'turn-2',
  title: 'Approve subagent edit?',
  options: [
    { id: 'approve', label: 'Approve', isApproval: true },
    { id: 'deny', label: 'Deny', isApproval: false },
  ],
  createdAtMs: 2,
};

const WORKER_APPROVAL: PendingApproval = {
  id: 'apr-worker', executionId: 'turn-3', sessionId: 'ses-worker', turnId: 'turn-3',
  title: 'Approve worker command?',
  options: [
    { id: 'approve', label: 'Approve', isApproval: true },
    { id: 'deny', label: 'Deny', isApproval: false },
  ],
  createdAtMs: 3,
};

describe('approvalsForSession', () => {
  const all = [LEAD_APPROVAL, SUBAGENT_APPROVAL, WORKER_APPROVAL];

  it('scopes Track approvals strictly to each owning session without cross-talk', () => {
    expect(approvalsForSession(all, 'ses-lead')).toEqual([LEAD_APPROVAL]);
    expect(approvalsForSession(all, 'ses-sub')).toEqual([SUBAGENT_APPROVAL]);
    expect(approvalsForSession(all, 'ses-other')).toBe(NO_PENDING_APPROVALS);
    expect(approvalsForSession(all, null)).toBe(NO_PENDING_APPROVALS);
  });

  it('prefers Worker execution-scoped approvals when supplied', () => {
    expect(approvalsForSession(all, 'ses-worker', [WORKER_APPROVAL])).toEqual([WORKER_APPROVAL]);
    expect(approvalsForSession(all, 'ses-worker', [])).toBe(NO_PENDING_APPROVALS);
  });
});

describe('decideApprovalOperation', () => {
  it('posts structured optionId to the execution approval decision endpoint', () => {
    const op = decideApprovalOperation('track/1', 'exec/2', 'apr/3', 'allow_once');
    expect(op.method).toBe('POST');
    expect(op.path).toBe('/api/v1/sessions/track%2F1/interactions/apr%2F3/respond');
    expect(op.body).toEqual({ executionId: 'exec/2', optionId: 'allow_once' });
    expect(op.responseSchema.safeParse({ id: LEAD_APPROVAL.id, kind: 'approval' }).success).toBe(true);
    expect(op.responseSchema.safeParse(undefined).success).toBe(false);
  });
});

describe('approvalDecisionFailureText', () => {
  const http = (status: number) => new ApiError({ kind: 'http', status, code: 'error', message: 'unknown option' });

  it('reports 409 as no longer pending, 4xx as server refusal, and transport failure as unconfirmed', () => {
    expect(approvalDecisionFailureText(http(409))).toBe(APPROVAL_GONE_TEXT);
    expect(approvalDecisionFailureText(http(400))).toBe('unknown option');
    expect(approvalDecisionFailureText(new ApiError({ kind: 'transport', message: 'dropped' })))
      .toBe('Sending your decision is unconfirmed.');
  });
});
