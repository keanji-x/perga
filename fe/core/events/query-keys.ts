import type { ModelCatalogScope } from '../domain/conversation.ts';

/**
 * Cache addresses for resources reachable by server events. Plans and query registrations
 * share these factories; prefix factories address every cached instance of a resource.
 * Pure values only: executing cache operations remains the platform adapter's responsibility.
 */
export const areas = () => ['areas'] as const;
export const trackLists = () => ['tracks'] as const;
export const tracksInArea = (areaId: string) => ['tracks', areaId] as const;
export const trackDetail = (trackId: string) => ['track', trackId] as const;
export const trackBacklinksPrefix = () => ['track-backlinks'] as const;
export const trackBacklinks = (trackId: string) => ['track-backlinks', trackId] as const;
export const trackReport = (trackId: string) => ['track-report', trackId] as const;
export const trackReportPrefix = () => ['track-report'] as const;
export const overlaysByKind = (entityKind: 'track' | 'card') => ['overlays', entityKind] as const;
export const plugins = () => ['plugins'] as const;
export const pluginDataPrefix = (id: string) => ['plugin-data', id] as const;
export const trackTemplates = () => ['track-templates'] as const;
export const transcriptPrefix = () => ['harness-items'] as const;
export const transcript = (cardId: string) => ['harness-items', cardId] as const;
export const plannerRun = (cardId: string) => ['planner-run', cardId] as const;
export const modelCatalog = (scope: ModelCatalogScope) => ['model-catalog', scope] as const;
export const modelCatalogForCard = (cardId: string) => ['model-catalog', { kind: 'card', cardId }] as const;
export const modelCatalogPrefix = () => ['model-catalog'] as const;
export const trackDetailPrefix = () => ['track'] as const;
export const sessionLive = (sessionId: string) => ['session-live', sessionId] as const;
export const trackTerminals = (trackId: string) => ['track-terminals', trackId] as const;
export const taskAttemptsPrefix = (trackId: string) => ['task-attempts', trackId] as const;

export const trackSchedules = (trackId: string) => ['track-schedules', trackId] as const;
export const trackTemplate = (trackId: string) => ['track-template', trackId] as const;
