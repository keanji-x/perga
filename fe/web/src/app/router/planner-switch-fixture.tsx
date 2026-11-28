import { sessionPage, sessionItem } from './conversation-session-data.ts';
import { fixtureReport, fixtureTrack, fixtureSession, fixtureSnapshot } from './session-contract-fixture.ts';
import type { ApiRequest, ApiTransportResponse } from '../../../../core/api/types.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { conversationCardId } from './conversation-read-data.ts';

export function renderPlannerSwitchFixture(options: Readonly<{
  initial?: string;
  reply?: (request: ApiRequest) => ApiTransportResponse | Promise<ApiTransportResponse> | undefined;
  switch?: (request: ApiRequest, commit: () => void) => ApiTransportResponse | Promise<ApiTransportResponse>;
}> = {}) {
  let switched = false;
  const track = () => fixtureTrack('daily', { title: 'Switch Track', leadSessionId: switched ? 'second-planner' : 'daily-planner' });
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  const sessions = () => ['daily-planner', 'second-planner'].map((id, index) => fixtureSession(id, 'daily', {
    title: index === 0 ? 'Daily Planner conversation' : 'Other conversation', isLead: switched ? index === 1 : index === 0,
  }));
  const receipt = () => ok(track());
  const snapshot = () => fixtureSnapshot(track(), sessions(), fixtureReport('# Project notes'));
  const fixture = renderDailyFixture({ initial: options.initial ?? '/track/daily?panel=conversations', extraPlanner: true,
    reply: async request => {
      const custom = await options.reply?.(request);
      if (custom !== undefined) return custom;
      if (request.method === 'PATCH' && request.path === '/api/v1/tracks/daily' && typeof request.body === 'object' && request.body !== null && 'leadSessionId' in request.body) {
        const commit = () => { switched = true; };
        if (options.switch) return options.switch(request, commit);
        commit(); return receipt();
      }
      if (request.path === '/api/v1/tracks/daily') return ok(snapshot());
      if (request.path === '/api/v1/tracks/daily/sessions') return ok(sessions());
      if (request.path.includes('/items?')) {
        const id = conversationCardId(request.path);
        return ok(sessionPage(sessions().find(session => session.id === id)!, [sessionItem(id, 1, 'Ready for switching')]));
      }
      return undefined;
    } });
  return { ...fixture, commit: () => { switched = true; }, receipt, sessions, snapshot };
}
