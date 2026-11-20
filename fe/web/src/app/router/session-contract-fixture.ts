import type { BootstrapDto, AgentSessionDto, ReportDocumentDto, SessionCreationReceipt, TrackAggregateSnapshotDto, TrackDto } from '../../../../core/api/perga-v1.ts';

export function fixtureTrack(id: string, overrides: Partial<TrackDto> = {}): TrackDto {
  return { id, areaId: 'daily-area', title: 'Track', cwd: '/tmp', workspaceDriver: 'direct', workspaceSpec: {},
    templateInputs: {}, templateState: {}, enabledPlugins: [], sortOrder: 0, createdAtMs: 1, updatedAtMs: 2, ...overrides };
}

export function fixtureSession(id: string, trackId: string, overrides: Partial<AgentSessionDto> = {}): AgentSessionDto {
  return { id, trackId, isLead: false, provider: 'codex', title: null, model: null, nativeThreadId: null, epoch: 1,
    status: 'idle', lastNotifiableTurnAtMs: null, contextUsage: null, archivedAtMs: null, createdAtMs: 1, updatedAtMs: 2, ...overrides };
}

export function fixtureReport(markdown = '', docRev = 1): ReportDocumentDto {
  return { docRev, summary: '', leadingComment: '', blocks: markdown === '' ? [] : [{ id: 'prose', rev: docRev, kind: 'prose', markdown, payload: null }] };
}

export function fixtureSnapshot(track: TrackDto, sessions: AgentSessionDto[] = [], report = fixtureReport()): TrackAggregateSnapshotDto {
  return { track, report, sessions, tasks: [], openAsks: [] };
}

export function fixtureCreation(session: AgentSessionDto): SessionCreationReceipt {
  return { session, firstInput: { inputId: 'first-input', turnId: 'first-turn', sessionId: session.id,
    deliveryState: 'accepted', turnStatus: 'queued', statusUrl: `/api/v1/sessions/${session.id}/inputs/first-input` } };
}

export function fixtureBootstrap(providers = ['codex'], models: string[] = []): BootstrapDto {
  return { version: 'browser-test', providers: providers.map(provider => ({ provider, available: true, version: 'test', reason: null, models, modelCatalog: [], permissionModes: [] })), templates: [], areas: [] };
}
