import type { BootstrapDto, AgentSessionDto, ReportDocumentDto, SessionCreationReceipt, TrackAggregateSnapshotDto, TrackDto } from '../../../../core/api/perga-v1.ts';

export function fixtureTrack(id: string, overrides: Partial<TrackDto> = {}): TrackDto {
  return { id, areaId: 'daily-area', title: 'Track', cwd: '/tmp', workspaceDriver: 'direct', workspaceSpec: {},
    templateInputs: {}, data: {}, dataRev: 0, flowRev: 0, enabledPlugins: [], sortOrder: 0, createdAtMs: 1, updatedAtMs: 2, ...overrides };
}

export function fixtureSession(id: string, trackId: string, overrides: Partial<AgentSessionDto> = {}): AgentSessionDto {
  return { id, trackId, isLead: false, provider: 'codex', title: null, model: null, nativeThreadId: null, epoch: 1,
    spawnDepth: 0, configVersion: 0, notifyParent: 'none', reconnectInvalidated: false, status: 'idle', lastNotifiableTurnAtMs: null, contextUsage: null, archivedAtMs: null, createdAtMs: 1, updatedAtMs: 2, ...overrides };
}

export function fixtureReport(markdown = '', docRev = 1): ReportDocumentDto {
  return { docRev, summary: '', leadingComment: '', blocks: markdown === '' ? [] : [{ id: 'prose', rev: docRev, kind: 'prose', markdown, payload: null }] };
}

export function fixtureWidgetReport(sections: ReadonlyArray<{ title: string; use: string; view: string }>): ReportDocumentDto {
  return {
    ...fixtureReport(),
    blocks: sections.flatMap(({ title, use, view }) => [
      { id: `${view}-heading`, rev: 1, kind: 'prose', markdown: `# ${title}`, payload: null },
      { id: view, rev: 1, kind: 'widget', markdown: '', payload: { use, view } },
    ]),
  };
}
export function fixtureSnapshot(track: TrackDto, sessions: AgentSessionDto[] = [], report = fixtureReport()): TrackAggregateSnapshotDto {
  return { track, report, sessions, tasks: [], openAsks: [] };
}

export function fixtureCreation(session: AgentSessionDto): SessionCreationReceipt {
  return { session, firstInput: { inputId: 'first-input', turnId: 'first-turn', sessionId: session.id,
    deliveryState: 'accepted', turnStatus: 'queued' } };
}

export function fixtureBootstrap(providers = ['codex'], models: string[] = []): BootstrapDto {
  return { version: 'browser-test', providers: providers.map(provider => ({ provider, available: true, version: 'test', reason: null, models, modelCatalog: [], permissionModes: [] })), templates: [], areas: [] };
}
