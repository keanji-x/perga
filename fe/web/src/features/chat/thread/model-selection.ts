import type { AgentProvider } from '../../../../../core/domain/presentation-types.ts';
import { type ProviderAvailability } from '../../../../../core/domain/agent-providers.ts';
import { providerPresentation } from '../../../../../core/domain/provider-presentation.ts';
import type { ModelCatalog } from './../../../../../core/domain/conversation-models.ts';

export const FOLLOW_DEFAULT_LABEL = 'Default';

/**
 * One provider's section of the menu: its catalog, `null` while it loads, and whether the provider can run
 * right now (`GET /api/v1/bootstrap`). `availability` is `null` while that answer is unknown, and for a
 * conversation that already exists: its turns keep issue-time handling (#1817).
 */
export type ModelGroup<Provider extends string = AgentProvider> = Readonly<{
  provider: Provider;
  catalog: ModelCatalog | null;
  availability: ProviderAvailability | null;
  /** Server-owned blank track creation capability; omitted for live model changes. */
  acceptsUnavailableBlankTrack?: boolean;
  /** Explicit host admission, independent of the Planner create policy; undefined keeps its existing behavior. */
  selectionProblem?: string | null;
  /** A provider can expose a current selection without a selectable default. */
  defaultAvailable?: boolean;
}>;


/** Managed display needs no provider-specific UI registration. */
export function providerLabel<Provider extends string>(_group: ModelGroup<Provider> | undefined, provider: Provider): string {
  return providerPresentation(provider).label;
}

export type CatalogEntry = ModelCatalog['models'][number];

/**
 * The groups a menu shows. The selection's own group always shows, so a pick never passes for another
 * provider's. Any other group is hidden while its provider is `not_configured` on this server.
 */
export function visibleModelGroups<Provider extends string>(groups: readonly ModelGroup<Provider>[], provider: Provider): readonly ModelGroup<Provider>[] {
  return groups.filter((group) => group.selectionProblem !== undefined || group.provider === provider || (
    group.availability?.status !== 'not_configured'
    && (!providerPresentation(group.provider)?.hiddenUntilKnown || (group.availability !== null && group.catalog !== null))));
}

/** The name of the default a catalog says is followed (for Claude, the model its CLI default resolves to), or `null` when it cannot say. */
export function defaultNameOf(catalog: ModelCatalog | null): string | null {
  return catalog?.default_source === 'config_read' || catalog?.default_source === 'config_toml'
    || catalog?.default_source === 'claude_cli' || catalog?.default_source === 'acp_session'
    ? catalog.default.model
    : null;
}

export function catalogReason<Provider extends string>(group: ModelGroup<Provider>): {
  reason: string | null;
  refused: boolean;
  blocked: string | null;
} {
  const reason = group.selectionProblem === undefined
    ? (group.availability?.status === 'unavailable' ? group.availability.reason : null)
    : group.selectionProblem;
  const refused = group.selectionProblem !== undefined || group.acceptsUnavailableBlankTrack !== true;
  const blocked = refused ? reason : null;
  return { reason, refused, blocked };
}

export function effortsFor<Provider extends string>(
  group: ModelGroup<Provider> | undefined,
  modelId: string | null,
): {
  efforts: CatalogEntry['supported_reasoning_efforts'];
  effortDefault: string | null;
} {
  const catalog = group?.catalog ?? null;
  const models = catalog?.models ?? [];
  const followed = catalog?.default.model == null
    ? undefined
    : models.find((model) => model.model === catalog.default.model || model.resolved_model === catalog.default.model);
  const chosen = modelId === null
    ? followed
    : models.find((model) => model.model === modelId);
  const defaultEfforts = catalog?.default.supported_reasoning_efforts;
  const efforts = (modelId === null && defaultEfforts != null && defaultEfforts.length > 0
    ? defaultEfforts
    : chosen?.supported_reasoning_efforts) ?? [];
  const effortDefault = modelId === null
    ? catalog?.default.reasoning_effort !== undefined ? catalog.default.reasoning_effort : chosen?.default_reasoning_effort ?? null
    : chosen?.default_reasoning_effort ?? null;
  return { efforts, effortDefault };
}
