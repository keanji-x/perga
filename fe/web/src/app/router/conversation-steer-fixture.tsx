import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import type { AgentProvider } from '../../../../core/api/generated/wire.ts';
import { sessionItem, sessionPage, sessionTurn } from './conversation-session-data.ts';
import { fixtureBootstrap, fixtureSession } from './session-contract-fixture.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
export const steerAnswer = (provider: AgentProvider) => fixtureBootstrap([provider]);
export function mountSteerFixture(provider: AgentProvider, managed: () => Promise<ApiTransportResponse> | ApiTransportResponse) {
  const session = fixtureSession('daily-planner', 'daily', { provider, status: 'running' });
  return renderDailyFixture({ initial: '/track/daily?panel=conversations', plannerProvider: provider, reply: request => {
    if (request.path.startsWith('/api/v1/bootstrap?scope=managed')) return managed();
    if (request.path.includes('/sessions/daily-planner/items?')) return ok(sessionPage(session,
      [sessionItem(session.id, 1, 'Stored words', 'user')], sessionTurn(session.id)));
    return undefined;
  } });
}
