import {
areas,
transcript as harnessItems,
modelCatalog,modelCatalogForCard,
modelCatalogPrefix,
overlaysByKind,
plannerRun,
readReceipts,
sessionLive,
trackBacklinks,
trackBacklinksPrefix,
trackDetail,
trackDetailPrefix,
trackLists,
tracksInArea,
trackTemplates,
trackTerminals,
trackViews,trackViewsPrefix,
transcriptPrefix,
} from '../../../../core/events/query-keys.ts';

// Key shapes match the legacy app so a cache dump reads the same in both.
export const queryKeys = Object.freeze({
  executionTargets: () => ['execution-targets'] as const,
  systemInfo: () => ['system-info'] as const,
  managedProviders: (targetId?: string) => targetId !== undefined && targetId !== '' && targetId !== 'local'
    ? ['managed-providers', targetId] as const : ['managed-providers'] as const,
  trackWorkspaceOptions: (areaId: string, targetId: string, cwd: string) => ['track-workspace-options', areaId, targetId, cwd] as const,
  dailyPlanner: (date: string) => ['daily-planner', date] as const,
  dailyCalendar: (start: string, end: string) => ['daily-calendar', start, end] as const,
  dailyReportChanges: (date: string) => ['daily-report-changes', date] as const,
  dailyReportEdits: (date: string, trackId: string, through: number) => ['daily-report-edits', date, trackId, through] as const,
  conversationHistory: (cardId: string) => [...harnessItems(cardId), 'conversation_v1'] as const,
  conversationLiveRead: (cardId: string, read: number, turnId: string | null) => ['harness-live', cardId, 'conversation_v1', read, turnId] as const,
  conversationLive: (cardId: string) => ['harness-live', cardId, 'conversation_v1'] as const,
  conversationRun: (cardId: string) => [...plannerRun(cardId), 'conversation_v1'] as const,
  serverVersion: () => ['server-version'] as const,
  areas,
  areaFolders: (areaId: string) => ['area-folders', areaId] as const,
  trackLists,
  tracksInArea,
  trackDetail,
  trackTools: (trackId: string) => [...trackDetail(trackId), 'tools'] as const,
  trackBacklinksPrefix,
  trackBacklinks,
  trackViews,
  trackViewsPrefix,
  trackSource: (trackId: string, sourceId: string) => ['track-source', trackId, sourceId] as const,
  overlaysByKind,
  /* The user's read receipts (#2596). Writes emit no event: the read refetches on focus and after each write. */
  readReceipts,
  /* Settings › Plugins. `plugin.state` invalidates; transient-state polling remains a supplement. */
  /* Plugin data changes invalidate every cached window belonging to that plugin. */
  /* One plugin's detail, read only by its configuration pane; the list carries no manifest by design. */
  /* The New track picker: `plugin.state` changes owner availability and input schemas. */
  trackTemplates,
  /* The user's own recipes. Recipe writes emit no `Event`; the mutations below invalidate this key directly. */
  transcriptPrefix,
  harnessItems,
  /** The running turn's streamed reply text (#1923). Polled while a reply may stream; no event reaches it. */
  harnessLive: (cardId: string) => ['harness-live', cardId] as const,
  plannerRun,
  /**
   * `GET /api/models` for one scope: a card (its default resolves against that card's workspace) or a
   * provider about to be created on. Turn completion refreshes the owning card scope.
   */
  modelCatalog,
  modelCatalogForCard,
  /** Every scope's catalog: a Recheck (#1822) re-fetches the Claude CLI's list behind the Claude ones. */
  modelCatalogPrefix,
  /** `GET /api/v1/bootstrap` provider availability. No event reaches it: the server's own 30 s cache is its freshness. */
  agentProviders: (targetId?: string) => targetId !== undefined && targetId !== '' && targetId !== 'local'
    ? ['agent-providers', targetId] as const : ['agent-providers'] as const,
  /** One track's conversation list, keyed by its track. */
  trackDetailPrefix,
  /**
   * The prefix for events that cannot name a track: the `runtime.*` kinds carry only a `card_id`, and a
   * card in a track nobody has open resolves to null. A fallback, not the house shape.
   */
  sessionLive,
  trackTerminals,
});
