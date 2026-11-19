import type { ApiRequest, ApiTransportResponse } from '../../../../../core/api/types.ts';
import type { SessionItemDto, SessionItemsPageDto } from '../../../../../core/api/perga-v1.ts';
import { fixtureSession, fixtureCreation, fixtureSnapshot, fixtureTrack } from '../session-contract-fixture.ts';
import { renderDailyFixture } from '../daily-planner-fixture.tsx';

export const FIRST_MESSAGE = 'Keep the first sentence readable through adoption.';

/** Real v1 creation receipt and item reads, independently delayed at the HTTP boundary. */
export function renderFirstMessageFixture({ mobile = false }: { mobile?: boolean } = {}) {
  const session = fixtureSession('ses_first_message', 'daily', { title: FIRST_MESSAGE });
  const receipt = fixtureCreation(session);
  const requests: ApiRequest[] = [];
  let releaseCreate = () => {};
  let releaseHistory = () => {};
  const createGate = new Promise<void>(resolve => { releaseCreate = resolve; });
  const historyGate = new Promise<void>(resolve => { releaseHistory = resolve; });
  let created = false;
  let reads = 0;
  const items: SessionItemDto[] = [{ id: 1, sessionId: session.id, turnId: receipt.firstInput!.turnId,
    kind: 'user', payload: { content: FIRST_MESSAGE, inputId: receipt.firstInput!.inputId }, createdAtMs: 2 }];
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  const view = renderDailyFixture({ initial: `/track/daily${mobile ? '' : '?panel=conversations'}`,
    reply: async request => {
      requests.push(request);
      if (request.path === '/api/v1/bootstrap') return ok({ version: 'first-message', providers: [], areas: [], templates: [] });
      if (request.path.startsWith('/api/v1/bootstrap?scope=managed')) return ok({ version: 'test', providers: [{ provider: 'codex', available: true, version: null, reason: null, models: [] }], templates: [], areas: [] });
      if (request.path === '/api/v1/tracks/daily/sessions' && request.method === 'POST') {
        await createGate; created = true;
        return { status: 201, statusText: 'Created', body: receipt };
      }
      if (created && request.path === '/api/v1/tracks/daily') return ok(fixtureSnapshot(fixtureTrack('daily'), [session]));
      if (created && request.path === '/api/v1/tracks/daily/sessions') return ok([session]);
      if (request.path.startsWith(`/api/v1/sessions/${session.id}/items?`)) {
        reads += 1; await historyGate;
        return ok({ session, activeTurn: null, unknownInput: null, items } satisfies SessionItemsPageDto);
      }
      if (request.path === receipt.firstInput!.statusUrl) return ok(receipt.firstInput);
      return undefined;
    },
  });
  return { ...view, requests, answerCreate: releaseCreate, releaseHistory,
    historyCount: () => reads };
}
