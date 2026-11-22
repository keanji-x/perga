import { onlineManager,QueryClient,QueryClientProvider } from '@tanstack/react-query';
import { cleanup,renderHook,waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach,expect,it,vi } from 'vitest';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { RecoveryAccess } from '../../../../core/domain/recovery/access.ts';
import { createRecoveryTransports } from '../../systems/recovery/transport.ts';
import { useTestConversationRegistry } from './permission-registry-fixture.ts';
import { usePlannerMutations } from './planner-mutations.ts';

afterEach(() => { cleanup(); onlineManager.setOnline(true); vi.unstubAllGlobals(); });
it('queued production model selections retain their original permit across recovery', async () => {
  vi.stubGlobal('__NC_BUNDLED__', true);
  const access = new RecoveryAccess(); access.change('connected');
  let finish!: (reply: ApiTransportResponse) => void;
  const send = vi.fn(() => new Promise<ApiTransportResponse>(resolve => { finish = resolve; }));
  const transport = createRecoveryTransports({ send }, access).business; const client = new QueryClient();
  const { result } = renderHook(() => usePlannerMutations(transport, 'card-a', 'track-a', createUnauthorizedChannel({ enqueue: task => task() }), useTestConversationRegistry()), {
    wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
  });
  const first = result.current.setModel({ model: 'first', reasoning_effort: null }).catch(error => error as unknown);
  const second = result.current.setModel({ model: 'second', reasoning_effort: null }).catch(error => error as unknown);
  await waitFor(() => expect(send).toHaveBeenCalledOnce()); access.invalidate('recovering'); access.change('connected');
  finish({ status: 200, statusText: 'OK', body: { id: 'card-a', trackId: 'track-a', provider: 'codex', model: 'first', createdAtMs: 1, updatedAtMs: 2 } });
  await first; await second; expect(send).toHaveBeenCalledOnce();
});
