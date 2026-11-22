import { performApiRequest } from '../../../../core/api/client.ts';
import type { ApiOperation,ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ApiError,classifyFailure,DELETE_FAILURES } from '../../../../core/domain/failure-class.ts';

/** TanStack Query wants a rejected promise; core reports failures as data. */
export async function runOperation<T>(
  transport: ApiTransportPort,
  operation: ApiOperation<T>,
  unauthorized: UnauthorizedChannel | undefined,
): Promise<T> {
  const checkpoint = transport.recovery?.checkpoint();
  const result = await performApiRequest(transport, operation, unauthorized);
  checkpoint?.();
  if (result.status === 'failed') throw new ApiError(result.error);
  return result.value;
}

/** A delete answered done (404, `DELETE_FAILURES`): the row is gone, so its cache entry goes as on success. */
export function deleteDone(error: unknown): boolean {
  return error instanceof ApiError && classifyFailure(error.failure, DELETE_FAILURES) === 'done';
}
