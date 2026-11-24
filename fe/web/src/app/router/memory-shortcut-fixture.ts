import { sessionInput, sessionItem, sessionPage, sessionReply } from './conversation-session-data.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureReport, fixtureSession, fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';

export function renderMemoryShortcutFixture({ closed = false, shared = false, today = false, hasTurns = false, failFirstDistill = false } = {}) {
  const trackId = today ? 'daily' : 'project';
  const areaId = today ? 'sys_daily' : 'project-area';
  const lead = fixtureSession(today ? 'daily-planner' : 'project-lead', trackId, { title: 'Lead', isLead: true });
  const track = fixtureTrack(trackId, { title: 'Review Track', areaId, leadSessionId: lead.id, closedAtMs: closed ? 10 : null });
  const globalId = 'trk_global_memory';
  const areaMemoryId = 'trk_area_memory';
  let distills = 0;
  return renderDailyFixture({
    initial: today ? '/' : '/track/project',
    shareScope: shared ? { trackId } : null,
    reply: request => {
      if (request.path === `/api/v1/tracks/${trackId}`) {
        return sessionReply(fixtureSnapshot(track, [lead], fixtureReport('# Release review\n\nReview notes.')));
      }
      if (request.path.startsWith(`/api/v1/sessions/${lead.id}/items?`)) {
        const page = sessionPage(lead, hasTurns ? [sessionItem(lead.id, 1, 'Review checklist agreed.', 'assistant_text')] : []);
        return sessionReply(closed ? { ...page, readonlyReason: 'track_closed' } : page);
      }
      if (request.path === `/api/v1/sessions/${lead.id}/messages`) return sessionReply(sessionInput(lead.id));
      if (request.path === '/api/v1/memory/distill') {
        if (++distills === 1 && failFirstDistill) {
          return { status: 409, statusText: 'Conflict', body: { error: 'The Memory Planner is temporarily unavailable.' } };
        }
        return sessionReply(today
          ? { scopeKey: 'global', areaId: null, memoryTrackId: globalId, leadSessionId: 'global-lead', mailId: 'mail_1' }
          : { scopeKey: `area:${areaId}`, areaId, memoryTrackId: areaMemoryId, leadSessionId: 'area-lead', mailId: 'mail_1' });
      }
      if (request.path === '/api/v1/memory/global/track') {
        return sessionReply({ scopeKey: 'global', areaId: null, trackId: globalId, leadSessionId: 'global-lead' });
      }
      if (request.path === `/api/v1/tracks/${globalId}`) {
        return sessionReply(fixtureSnapshot(fixtureTrack(globalId, { title: 'Global Memory', areaId: 'sys_memory' })));
      }
      if (request.path === `/api/v1/memory/areas/${areaId}/track`) {
        return sessionReply({ scopeKey: `area:${areaId}`, areaId, trackId: areaMemoryId, leadSessionId: 'area-lead' });
      }
      if (request.path === `/api/v1/tracks/${areaMemoryId}`) {
        return sessionReply(fixtureSnapshot(fixtureTrack(areaMemoryId, { title: 'Project Memory', areaId: 'sys_memory' })));
      }
      return undefined;
    },
  });
}
