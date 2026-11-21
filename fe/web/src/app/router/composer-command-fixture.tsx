import { sessionPage, sessionItem, sessionTurn, sessionInput } from './conversation-session-data.ts';
import { fixtureSnapshot, fixtureSession, fixtureTrack } from './session-contract-fixture.ts';
import type { ApiRequest, ApiTransportResponse } from '../../../../core/api/types.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { conversationCardId } from './conversation-read-data.ts';

export function renderCommandFixture(options: Readonly<{
  closed?: boolean; canClose?: boolean; canReopen?: boolean; running?: boolean;
  patch?: (request: ApiRequest) => ApiTransportResponse | Promise<ApiTransportResponse>;
  staleDetail?: boolean;
}> = {}) {
  let closed = options.closed ?? false;
  let track = fixtureTrack('daily', { areaId: 'daily-area', title: 'Command Track', cwd: '/tmp', sortOrder: 0, closedAtMs: closed ? 1 : null, createdAtMs: 1, updatedAtMs: 2 });
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  return renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: true, reply: async request => {
    if (request.method === 'PATCH' && request.path === '/api/v1/tracks/daily') {
      if (options.patch !== undefined) return options.patch(request);
      if (!options.staleDetail) {
        const body = request.body as { closed?: boolean };
        closed = body.closed ?? closed;
        track = { ...track, closedAtMs: closed ? 1 : null };
      }
      return ok(track);
    }
    if (request.path === '/api/v1/tracks/daily') return ok(fixtureSnapshot(track, [
      fixtureSession('daily-planner', track.id, { title: 'Daily Planner conversation', isLead: true }),
      fixtureSession('second-planner', track.id, { title: 'Other conversation' }),
    ]));
    if (request.path.includes('/items?')) {
      const id = conversationCardId(request.path);
      return ok(sessionPage(fixtureSession(id, 'daily'), [sessionItem(id, 1, 'Ready for commands')], options.running ? sessionTurn(id) : null));
    }
    if (request.path.endsWith('/control')) return ok(fixtureSession('daily-planner', 'daily'));
    if (request.path.endsWith('/messages')) return ok(sessionInput('daily-planner'));
    return undefined;
  } });
}
