import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { useTrackLifecycleMutations } from './track-lifecycle-mutations.ts';
import { useTrackToolMutations } from './track-tool-mutations.ts';
import { useTrackInputMutations } from './track-input-mutations.ts';
/** Composite for surfaces that own all Track actions; narrow surfaces use the owning hooks. */
export function useTrackMutations(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  const lifecycle = useTrackLifecycleMutations(transport, unauthorized);
  const tools = useTrackToolMutations(transport, unauthorized);
  const input = useTrackInputMutations(transport, unauthorized);
  return { ...lifecycle, ...tools, ...input };
}
export type TrackMutations = ReturnType<typeof useTrackMutations>;
