import type { SessionItemDto, SessionTurnDto } from '../../../../core/api/perga-v1.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureSession, fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';

import { sessionPage, sessionReply } from './conversation-session-data.ts';

/** Supply actual v1 responses at the production router boundary, including every items view. */
export function renderSessionFixture(options: NonNullable<Parameters<typeof renderDailyFixture>[0]> & { attachmentsSupported?: boolean } = {}) {
  const sessions = [fixtureSession('daily-planner', 'daily', { title: 'Daily Planner conversation', isLead: true }),
    fixtureSession('second-planner', 'daily', { title: 'Other conversation' })];
  const items = new Map<string, SessionItemDto[]>();
  const activeTurns = new Map<string, SessionTurnDto>();
  const view = renderDailyFixture({ ...options, extraPlanner: true, reply: async request => {
    const custom = await options.reply?.(request);
    if (custom !== undefined) return custom;
    if (request.path.startsWith('/api/v1/bootstrap')) return sessionReply({
      version: 'browser-fixture', providers: [{ provider: 'codex', available: true, version: 'test', reason: null, models: ['test-model'] }],
      templates: [], areas: [],
    });
    if (request.method === 'GET' && request.path === '/api/v1/tracks/daily') return sessionReply(fixtureSnapshot(
      fixtureTrack('daily', { title: 'Renamed daily Track', leadSessionId: 'daily-planner' }), [...sessions]));
    if (request.method === 'GET' && request.path === '/api/v1/tracks/daily/sessions') return sessionReply([...sessions]);
    const match = /^\/api\/v1\/sessions\/([^/]+)\/items\?/.exec(request.path);
    if (match !== null) {
      const id = decodeURIComponent(match[1]);
      const session = sessions.find(row => row.id === id);
      if (session === undefined) throw new Error(`Unknown fixture session ${id}`);
      const before = new URL(request.path, 'http://fixture').searchParams.get('beforeId');
      return sessionReply(sessionPage(session, (items.get(id) ?? []).filter(item => before === null || item.id < Number(before)).slice(-500),
        activeTurns.get(id) ?? null, undefined, options.attachmentsSupported));
    }
    return undefined;
  } });
  return { ...view, sessions, items, activeTurns };
}
