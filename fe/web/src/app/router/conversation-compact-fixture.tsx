import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import type { AgentProvider } from '../../../../core/api/generated/wire.ts';
import { fixtureSession } from './session-contract-fixture.ts';
import { sessionItem, sessionPage } from './conversation-session-data.ts';
import { conversationCardId } from './conversation-read-data.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

/** Complete v1 session responses; compact support is derived by the production reader. */
export function renderCompactFixture(provider: AgentProvider, historyReady = true, compactReply?: ApiTransportResponse) {
  const run = sessionPage(fixtureSession('daily-planner', 'daily', { provider }));
  return { run, ...renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: true, plannerProvider: provider, reply: request => {
    if (request.path.endsWith('/compact')) return compactReply ?? { status: 200, statusText: 'OK', body: { card_id: run.session.id, started: true } };
    if (request.path.includes('/items?')) {
      if (!request.path.includes('&view=') && !historyReady) return { status: 503, statusText: 'Unavailable', body: { error: 'internal', message: 'History unavailable' } };
      const id = conversationCardId(request.path);
      return { status: 200, statusText: 'OK', body: sessionPage({ ...run.session, id }, [
        sessionItem(id, 1, 'Earlier prompt.', 'user'), sessionItem(id, 2, 'Earlier reply.'),
      ]) };
    }
    return undefined;
  } }) };
}
