import { z } from 'zod';
import { gateVerdictDtoSchema, taskDtoSchema, type TaskDto } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';

export type PergaGateStepEvidence = Readonly<{
  name: string;
  cmd: string | null;
  exitCode: number;
  passed: boolean;
  durationMs: number;
  output: string;
}>;

export type TaskEvidence = Readonly<{
  workerResult: string | null;
  gatePassed: boolean | null;
  gateSteps: readonly PergaGateStepEvidence[];
}>;

export function taskEvidence(dto: TaskDto): TaskEvidence {
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

export function cancelTaskOperation(trackId: string, key: string): ApiOperation<TaskDto | undefined> {
  return {
    method: 'POST',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/tasks/${encodeURIComponent(key)}/cancel`,
    body: {},
    responseSchema: z.union([taskDtoSchema, z.undefined()]),
  };
}

/** Dispatch is preparation; only running means business execution has begun. */
export function taskStatusLabel(status: string): string {
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
