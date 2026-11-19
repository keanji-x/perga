import { fixtureSession } from './session-contract-fixture.ts';
import { sessionPage, sessionTurn } from './conversation-session-data.ts';
// Fault injection at the production router transport, shared by DOM and browser checks.
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

export function renderConversationReadFixture(initialFailure = true) {
  let unavailable = initialFailure;
  let historyUnavailable = false;
  let runGate: Promise<void> | null = null;
  const session = fixtureSession('daily-planner', 'daily', { status: 'running' });
  const run = sessionPage(session, [], sessionTurn(session.id));
  const failure: ApiTransportResponse = { status: 503, statusText: 'Unavailable',
    body: { error: 'internal', message: 'private transport diagnostic' } };
  const fixture = renderDailyFixture({ initial: '/track/daily?panel=conversations', reply: async (request) => {
    if (request.path.includes('&view=run')) {
      await runGate;
      return unavailable ? failure : { status: 200, statusText: 'OK', body: run };
    }
    if (request.path.includes('/items?') && !request.path.includes('&view=')) return historyUnavailable ? failure : { status: 200, statusText: 'OK', body: sessionPage(session, [{ id: 1, sessionId: session.id, turnId: 'turn', kind: 'assistant_text', payload: { text: 'Retained reply.' }, createdAtMs: 1 }]) };
    return undefined;
  } });
  return { ...fixture, pauseRun: () => {
    let release = () => {};
    runGate = new Promise<void>(resolve => { release = resolve; });
    return () => { runGate = null; release(); };
  }, failRun: (value: boolean) => { unavailable = value; },
    failHistory: (value: boolean) => { historyUnavailable = value; } };
}
