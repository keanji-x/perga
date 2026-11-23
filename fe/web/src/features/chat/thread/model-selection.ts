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

export function shortProviderLabel<Provider extends string>(
  group: ModelGroup<Provider>,
  provider: Provider,
  fallback?: string,
): string {
  return providerPresentation(provider).shortLabel ?? fallback ?? providerLabel(group, provider);
}

function isAcronymSubsequence(needle: string, words: readonly string[]): boolean {
  let cursor = 0;
  for (const word of words) {
    if (cursor >= needle.length) break;
    let wi = 0;
    while (wi < word.length && cursor < needle.length && word[wi] === needle[cursor]) {
      wi += 1;
      cursor += 1;
    }
  }
  return cursor === needle.length;
}

/** True when `a` and `b` are within 1 substitution, insertion, deletion, or adjacent transposition. */
function withinOneEdit(a: string, b: string): boolean {
  const diff = Math.abs(a.length - b.length);
  if (diff > 1) return false;
  if (a.length === b.length) {
    let first = -1;
    let mismatches = 0;
    for (let i = 0; i < a.length; i += 1) {
      if (a[i] !== b[i]) {
        mismatches += 1;
        if (first === -1) first = i;
        else if (mismatches === 2 && i === first + 1 && a[first] === b[i] && a[i] === b[first]) continue;
        else if (mismatches > 1) return false;
      }
    }
    return mismatches <= 2;
  }
  const [shorter, longer] = a.length < b.length ? [a, b] : [b, a];
  let si = 0;
  let li = 0;
  let skipped = false;
  while (si < shorter.length && li < longer.length) {
    if (shorter[si] === longer[li]) {
      si += 1;
      li += 1;
    } else if (!skipped) {
      skipped = true;
      li += 1;
    } else {
      return false;
    }
  }
  return true;
}

function scoreToken(token: string, raw: string, compact: string, words: readonly string[]): number {
  const compactToken = token.replace(/[^a-z0-9]+/g, '');
  if (words.some((word) => word.startsWith(token) || (compactToken !== '' && word.startsWith(compactToken)))) return 100;
  if (raw.includes(token)) return 80;
  if (compactToken !== '' && compact.includes(compactToken)) return 70;
  if (compactToken.length >= 2 && isAcronymSubsequence(compactToken, words)) return 60;
  if (compactToken.length >= 3) {
    const firstChar = compactToken[0];
    for (const word of words) {
      if (word.length < 3 || word[0] !== firstChar) continue;
      const prefix = word.slice(0, compactToken.length);
      if (withinOneEdit(compactToken, prefix)) return 50;
      if (word.length >= compactToken.length + 1 && withinOneEdit(compactToken, word.slice(0, compactToken.length + 1))) return 48;
      if (word.length >= compactToken.length - 1 && withinOneEdit(compactToken, word)) return 48;
    }
  }
  return 0;
}

/** Multi-token, punctuation-tolerant, subsequence + 1-typo fuzzy scorer for model search. Returns 0 when unmatched. */
export function scoreModelSearch(fields: readonly (string | null | undefined)[], query: string): number {
  const tokens = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (tokens.length === 0) return 1;
  const raw = fields.filter((part): part is string => typeof part === 'string' && part.length > 0).join(' ').toLowerCase();
  const compact = raw.replace(/[^a-z0-9]+/g, '');
  const words = raw.split(/[^a-z0-9]+/).filter(Boolean);
  let total = 0;
  for (const token of tokens) {
    const score = scoreToken(token, raw, compact, words);
    if (score === 0) return 0;
    total += score;
  }
  const primary = (fields[0] ?? '').toLowerCase();
  if (primary !== '' && tokens.some((token) => primary.includes(token))) total += 15;
  return total;
}
