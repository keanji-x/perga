import { z } from 'zod';
import { gateVerdictDtoSchema, taskAttemptDtoSchema, taskDtoSchema, type TaskAttemptDto, type TaskDto } from '../api/schemas.js';
import type { ApiOperation } from '../api/types.js';

/** Gate-free summaries of a task's execution history. Nullable fields are required. */
export const taskAttemptSchema = z.object({
  attempt_id: z.string().min(1),
  generation: z.number().int().positive(),
  status: z.string().min(1),
  status_detail: z.string().nullable(),
  blocking_reason: z.string().nullable(),
  worker_card_id: z.string().nullable(),
  created_at_ms: z.number(),
  finished_at_ms: z.number().nullable(),
});
export const taskRecoveryViewSchema = z.object({
  key: z.string().min(1),
  current: taskAttemptSchema.nullable(),
  attempts: z.array(taskAttemptSchema),
}).refine((view) => {
  if (view.current === null) return view.attempts.length === 0;
  const latest = view.attempts.at(-1);
  return latest?.attempt_id === view.current.attempt_id && latest.generation === view.current.generation;
}, { message: 'Task history has inconsistent current allocation evidence' });
export type TaskAttempt = z.infer<typeof taskAttemptSchema>;
export type TaskRecoveryView = z.infer<typeof taskRecoveryViewSchema>;

export type PergaGateStepEvidence = Readonly<{
  name: string;
  cmd: string | null;
  exitCode: number;
  passed: boolean;
  durationMs: number;
  output: string;
}>;

export type PergaAttemptEvidence = Readonly<{
  workerResult: string | null;
  gatePassed: boolean | null;
  gateSteps: readonly PergaGateStepEvidence[];
}>;

export type PergaTaskRecoveryView = TaskRecoveryView & Readonly<{
  evidenceByAttemptId?: Readonly<Record<string, PergaAttemptEvidence>>;
}>;

function extractAttemptEvidence(dto: TaskAttemptDto): PergaAttemptEvidence {
  const verdictParsed = gateVerdictDtoSchema.safeParse(dto.gateVerdict);
  const gateSteps: PergaGateStepEvidence[] = verdictParsed.success
    ? verdictParsed.data.steps.map((step) => {
      const stdout = step.stdout?.trim() ?? '';
      const stderr = step.stderr?.trim() ?? '';
      const combined = [stdout, stderr].filter((part) => part !== '').join('\n');
      return {
        name: step.name,
        cmd: step.cmd ?? null,
        exitCode: step.exitCode,
        passed: step.passed ?? step.exitCode === 0,
        durationMs: step.durationMs,
        output: combined !== '' ? combined : step.outputTail,
      };
    })
    : [];
  const workerResult = dto.workerResult === null || dto.workerResult === undefined
    ? null
    : typeof dto.workerResult === 'string'
      ? dto.workerResult
      : JSON.stringify(dto.workerResult, null, 2);
  return {
    workerResult,
    gatePassed: verdictParsed.success ? verdictParsed.data.passed : null,
    gateSteps,
  };
}

function mapAttemptDtos(key: string, dtos: readonly TaskAttemptDto[]): Readonly<{
  view: TaskRecoveryView;
  evidenceByAttemptId: Readonly<Record<string, PergaAttemptEvidence>>;
}> {
  const ordered = [...dtos].sort((left, right) => left.startedAtMs - right.startedAtMs);
  const evidenceByAttemptId: Record<string, PergaAttemptEvidence> = {};
  const attempts: TaskAttempt[] = ordered.map((dto, index) => {
    const evidence = extractAttemptEvidence(dto);
    evidenceByAttemptId[dto.attemptId] = evidence;
    const failedStep = evidence.gatePassed === false
      ? evidence.gateSteps.find((step) => !step.passed)?.name ?? null
      : null;
    return {
      attempt_id: dto.attemptId,
      generation: index + 1,
      status: dto.status.toLowerCase(),
      status_detail: failedStep !== null ? `Gate step failed: ${failedStep}` : null,
      blocking_reason: null,
      worker_card_id: dto.sessionId ?? null,
      created_at_ms: dto.startedAtMs,
      finished_at_ms: dto.finishedAtMs ?? null,
    };
  });
  return {
    view: {
      key,
      current: attempts.at(-1) ?? null,
      attempts,
    },
    evidenceByAttemptId,
  };
}

export function taskAttemptsOperation(trackId: string, key: string): ApiOperation<TaskRecoveryView> {
  return {
    method: 'GET',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/tasks/${encodeURIComponent(key)}/attempts`,
    responseSchema: z.union([
      z.array(taskAttemptDtoSchema).transform((dtos) => mapAttemptDtos(key, dtos).view),
      taskRecoveryViewSchema.refine((view) => view.key === key,
        { message: 'Task history belongs to a different task' }),
    ]),
  };
}

export function pergaTaskAttemptsOperation(trackId: string, key: string): ApiOperation<PergaTaskRecoveryView> {
  return {
    method: 'GET',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/tasks/${encodeURIComponent(key)}/attempts`,
    responseSchema: z.union([
      z.array(taskAttemptDtoSchema).transform((dtos) => {
        const mapped = mapAttemptDtos(key, dtos);
        return { ...mapped.view, evidenceByAttemptId: mapped.evidenceByAttemptId };
      }),
      taskRecoveryViewSchema.refine((view) => view.key === key,
        { message: 'Task history belongs to a different task' }),
    ]),
  };
}

export function cancelTaskOperation(trackId: string, key: string): ApiOperation<TaskDto | undefined> {
  return {
    method: 'POST',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/tasks/${encodeURIComponent(key)}/cancel`,
    body: {},
    responseSchema: z.union([taskDtoSchema, z.undefined()]),
  };
}

export function acceptTaskOperation(trackId: string, key: string, note?: string): ApiOperation<TaskDto | undefined> {
  return {
    method: 'POST',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/tasks/${encodeURIComponent(key)}/accept`,
    body: note === undefined ? {} : { note },
    responseSchema: z.union([taskDtoSchema, z.undefined()]),
  };
}

export function rejectTaskOperation(trackId: string, key: string, note?: string): ApiOperation<TaskDto | undefined> {
  return {
    method: 'POST',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/tasks/${encodeURIComponent(key)}/reject`,
    body: note === undefined ? {} : { note },
    responseSchema: z.union([taskDtoSchema, z.undefined()]),
  };
}

/** Dispatch is preparation; only running means business execution has begun. */
export function attemptStatusLabel(status: string): string {
  switch (status.toLowerCase()) {
    case 'awaiting_projection': return 'Waiting to start';
    case 'pending': return 'Queued';
    case 'dispatched': return 'Preparing';
    case 'running': return 'Running';
    case 'verifying': return 'Checking result';
    case 'done': return 'Completed';
    case 'failed': return 'Failed';
    case 'canceled': return 'Canceled';
    case 'interrupted': return 'Interrupted';
    default: return status;
  }
}
