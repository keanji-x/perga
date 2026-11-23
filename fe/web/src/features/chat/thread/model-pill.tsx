import { providerPresentation } from '../../../../../core/domain/provider-presentation.ts';
import { catalogReason, defaultNameOf, effortsFor, FOLLOW_DEFAULT_LABEL, providerLabel, scoreModelSearch, shortProviderLabel, visibleModelGroups, type CatalogEntry, type ModelGroup } from './model-selection.ts';
// The model picker in a planner conversation's composer footer, and on the new-track page. Presentational:
// every value is a prop; the queries and the write live in `app/router`. Each provider's catalog is one
// group; on the new-track page the pick also decides the Planner's provider (#1810), and each group
// follows its provider's availability (#1817). A Claude group is the Claude CLI's own list (#1822).
// Beside it, a Planner conversation's approval setting (#2348), and the Planner's role on the new-track page and in
// an existing Planner conversation, triggers of the same kind.

import { Button } from '@astryxdesign/core/Button';
import { HStack } from '@astryxdesign/core/HStack';
import { Icon as AstryxIcon } from '@astryxdesign/core/Icon';
import { Popover } from '@astryxdesign/core/Popover';
import { Text } from '@astryxdesign/core/Text';
import { Tooltip } from '@astryxdesign/core/Tooltip';
import { VisuallyHidden } from '@astryxdesign/core/VisuallyHidden';

import { useRef, type ReactNode } from 'react';

import type { AgentProvider } from '../../../../../core/domain/presentation-types.ts';
import { unavailableCreationNote } from '../../../../../core/domain/agent-providers.ts';
import type { ModelSelection } from '../../../../../core/domain/conversation.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import styles from './model-pill.module.css';
// Keep the shared CSS after Astryx imports: importing the slider earlier changes layer order.
import { EffortSlider } from './model-effort-slider.tsx';
import { PERMISSION_NOTE, PermissionModePill, useEscapeFocus } from './setting-pill.tsx';

export { PermissionModePill };

export function ModelPill<Provider extends string = AgentProvider>({
  groups, provider, selection, onChange, isDisabled = false, placement = 'above', triggerId, effortControl = 'separate', appliesNextTurn = false,
}: Readonly<{
  /** What can be chosen, one group per provider, in menu order. A conversation passes only its own provider's. */
  groups: readonly ModelGroup<Provider>[];
  /** The provider `selection` belongs to. */
  provider: Provider;
  /** What this conversation has chosen. `null` in both fields is "follow the default". */
  selection: ModelSelection;
  /** A pick in another group also hands back that group's provider. */
  onChange: (next: ModelSelection, provider: Provider) => void;
  isDisabled?: boolean;
  placement?: 'above' | 'below';
  triggerId?: string;
  /** Kept for caller compatibility; all surfaces render the unified two-column + effort slider picker. */
  effortControl?: 'separate' | 'in-menu';
  appliesNextTurn?: boolean;
}>) {
  const [open, setOpen] = useState(false);
  const [providerFilter, setProviderFilter] = useState<Provider | 'all'>('all');
  const [searchQuery, setSearchQuery] = useState('');
  const [hoveredEffortIndex, setHoveredEffortIndex] = useState<number | null>(null);
  const [draft, setDraft] = useState<Readonly<{ provider: Provider; selection: ModelSelection }>>({
    provider,
    selection,
  });
  const hostRef = useRef<HTMLSpanElement | null>(null);
  const searchInputRef = useRef<HTMLInputElement | null>(null);
  const { onEscape, onOpenChange } = useEscapeFocus(open, setOpen, hostRef);

  const shown = visibleModelGroups(groups, provider);
  const grouped = shown.length > 1 || shown.some(group => group.selectionProblem !== undefined);
  const activeGroup = groups.find((group) => group.provider === provider);
  const catalog = activeGroup?.catalog ?? null;
  const models = catalog?.models ?? [];
  const unreachable = shown.length > 0
    && shown.every((group) => group.catalog !== null && group.catalog.source === 'unavailable');
  const defaultName = defaultNameOf(catalog);
  const followed = catalog?.default.model == null
    ? undefined
    : models.find((model) => model.model === catalog.default.model);
  const chosen = selection.model === null
    ? followed
    : models.find((model) => model.model === selection.model);
  const defaultEntry = defaultName === null ? undefined : models.find((model) => model.resolved_model === defaultName);
  const named = selection.model === null
    ? (defaultEntry?.display_name ?? defaultName ?? FOLLOW_DEFAULT_LABEL)
    : (chosen?.display_name ?? selection.model);
  const { efforts, effortDefault } = effortsFor(activeGroup, selection.model);
  const activeEffortLabel = selection.reasoning_effort ?? effortDefault;
  const baseLabel = grouped ? `${providerLabel(activeGroup, provider)} ${named}` : named;
  const label = efforts.length > 0 && activeEffortLabel !== null ? `${baseLabel} · ${activeEffortLabel}` : baseLabel;

  const spokenLabel = selection.model === null && defaultName !== null
    ? `Model: ${label} (this installation's default)`
    : `Model: ${label}`;

  const triggerDisabled = isDisabled
    || (unreachable && !shown.some(group => group.selectionProblem === null || group.acceptsUnavailableBlankTrack === true));

  const activeProviderGroup = shown.find((group) => group.provider === draft.provider)
    ?? shown.find((group) => group.provider === provider)
    ?? shown[0];
  const filteredGroup = providerFilter === 'all'
    ? null
    : shown.find((group) => group.provider === providerFilter) ?? null;

  const handleOpenChange = (nextOpen: boolean) => {
    setHoveredEffortIndex(null);
    setSearchQuery('');
    if (nextOpen) {
      setProviderFilter(shown.length > 1 ? 'all' : (shown[0]?.provider ?? 'all'));
      setDraft({ provider, selection });
    }
    onOpenChange(nextOpen);
  };

  const totalModelCount = shown.reduce((sum, group) => sum + (group.catalog?.models.length ?? 0), 0);
  const showSearch = totalModelCount > 6;
  const normalizedQuery = searchQuery.trim().toLowerCase();

  // Anchor the Effort band to the actual draft provider/model so browsing another
  // provider filter chip cannot bypass `defaultAvailable: false` or mismatch Save.
  const draftGroup = shown.find((group) => group.provider === draft.provider) ?? activeProviderGroup;
  const draftModelId = draft.selection.model;
  const draftEffortValue = draft.selection.reasoning_effort;
  const savedEffort = draftGroup?.provider === provider && draftModelId === selection.model
    ? selection.reasoning_effort : null;
  const retainedEffort = draftEffortValue ?? savedEffort;
  const draftEffortInfo = effortsFor(draftGroup, draftModelId);
  const draftReason = draftGroup ? catalogReason(draftGroup) : { reason: null, refused: false, blocked: null };
  const draftUnavailable = draftGroup === undefined
    || draftReason.blocked !== null
    || draftGroup.availability?.status === 'unavailable'
    || draftGroup.catalog?.source === 'unavailable';
  const draftBlocked = draftGroup === undefined
    || draftReason.blocked !== null
    || (draft.selection.model === null && draftGroup.defaultAvailable === false);

  const effortStops = draftEffortInfo.efforts.map((entry) => ({
    value: entry.reasoning_effort,
    label: entry.reasoning_effort,
  }));
  const defaultEffort = draftEffortInfo.effortDefault;
  const defaultEffortIndex = defaultEffort === null ? -1 : effortStops.findIndex(stop => stop.value === defaultEffort);
  const selectedEffortIndex = draftEffortValue === null ? -1 : effortStops.findIndex(stop => stop.value === draftEffortValue);
  const hasEffortChoices = effortStops.length > 0;
  const unavailableEffort = draftEffortValue !== null && selectedEffortIndex === -1 ? draftEffortValue : null;
  const effortValueText = unavailableEffort !== null
    ? `${unavailableEffort} (Unavailable)`
    : selectedEffortIndex >= 0
      ? effortStops[selectedEffortIndex].label
      : !hasEffortChoices && retainedEffort === null
        ? 'N/A'
        : defaultEffortIndex >= 0
          ? `${FOLLOW_DEFAULT_LABEL} (${effortStops[defaultEffortIndex].label})`
          : FOLLOW_DEFAULT_LABEL;
  const hoveredEffort = hoveredEffortIndex !== null ? effortStops[hoveredEffortIndex] ?? null : null;
  const canResetEffort = draftEffortValue !== null && draftReason.blocked === null;
  const resetEffort = () => {
    setDraft({
      provider: draftGroup?.provider ?? draft.provider,
      selection: { model: draftModelId, reasoning_effort: null },
    });
  };

  const selectModel = (targetProvider: Provider, model: string | null) => {
    if (draft.provider === targetProvider && draft.selection.model === model) return;
    setDraft({ provider: targetProvider, selection: { model, reasoning_effort: null } });
  };

  const popoverContent = draftGroup === undefined ? null : (
    <div
      className={styles.pickerPanel}
      role="presentation"
      /* Stop React synthetic events from bubbling into a host ChatComposer, whose click handler yanks focus back to the message editor. */
      onMouseDown={(event) => event.stopPropagation()}
      onClick={(event) => event.stopPropagation()}
    >
      {(showSearch || shown.length > 1) && (
        <div className={styles.filterHeader}>
          {showSearch && (
            <div className={styles.searchWrap}>
              <label className={styles.searchField}>
                <span className={styles.searchIcon} aria-hidden="true"><Icon name="search" size="sm" /></span>
                <input
                  ref={searchInputRef}
                  type="search"
                  className={styles.searchInput}
                  value={searchQuery}
                  aria-label="Filter models"
                  placeholder="Search models or providers…"
                  onChange={(event) => setSearchQuery(event.target.value)}
                />
              </label>
              {searchQuery !== '' && (
                <button
                  type="button"
                  className={styles.searchClear}
                  aria-label="Clear search"
                  onClick={() => {
                    setSearchQuery('');
                    searchInputRef.current?.focus();
                  }}
                >
                  <Icon name="close" size="sm" />
                </button>
              )}
            </div>
          )}
          {shown.length > 1 && (
            <div className={styles.providerFilterBar} role="group" aria-label="Providers">
              <button
                type="button"
                role="radio"
                aria-checked={providerFilter === 'all'}
                data-nc-active={providerFilter === 'all' ? 'true' : 'false'}
                className={styles.filterChip}
                onClick={() => setProviderFilter('all')}
              >
                All
              </button>
              {shown.map((group) => {
                const heading = providerLabel(group, group.provider);
                const isActiveFilter = providerFilter === group.provider;
                const isUnavailable = catalogReason(group).blocked !== null;
                return (
                  <button
                    key={group.provider}
                    type="button"
                    role="radio"
                    aria-checked={isActiveFilter}
                    data-nc-active={isActiveFilter ? 'true' : 'false'}
                    data-nc-unavailable={isUnavailable ? 'true' : 'false'}
                    className={styles.filterChip}
                    onClick={() => {
                      setProviderFilter(group.provider);
                      if (group.provider !== draft.provider && !isUnavailable) {
                        if (group.provider === provider) {
                          setDraft({ provider: group.provider, selection });
                        } else if (group.defaultAvailable !== false) {
                          setDraft({ provider: group.provider, selection: { model: null, reasoning_effort: null } });
                        }
                      }
                    }}
                  >
                    {shortProviderLabel(group, group.provider, heading)}
                  </button>
                );
              })}
            </div>
          )}
        </div>
      )}
      <div className={styles.modelList} role="group" aria-label="Models">
        {filteredGroup !== null ? (
          <GroupChoices
            group={filteredGroup}
            selection={filteredGroup.provider === draft.provider ? draft.selection : null}
            query={normalizedQuery}
            onClearFilter={normalizedQuery !== '' ? () => setProviderFilter('all') : undefined}
            onChange={(model) => selectModel(filteredGroup.provider, model)}
          />
        ) : shown.length === 1 ? (
          <GroupChoices
            group={shown[0]}
            selection={shown[0].provider === draft.provider ? draft.selection : null}
            query={normalizedQuery}
            onChange={(model) => selectModel(shown[0].provider, model)}
          />
        ) : (
          <UnifiedModelList
            groups={shown}
            activeGroup={activeProviderGroup}
            draft={draft}
            query={normalizedQuery}
            onSelect={selectModel}
          />
        )}
      </div>
      {(!draftUnavailable || hasEffortChoices || canResetEffort) && (
        <div className={styles.effortBand}>
          <div className={styles.effortRow}>
            <span className={styles.effortTitle}>Effort</span>
            <EffortSlider
              stops={effortStops}
              selectedIndex={selectedEffortIndex}
              defaultIndex={defaultEffortIndex}
              valueText={effortValueText}
              isDisabled={(!hasEffortChoices && !canResetEffort) || draftReason.blocked !== null}
              onChangeIndex={(idx) => {
                const stop = effortStops.at(idx);
                if (!stop) return;
                setDraft({
                  provider: draftGroup.provider,
                  selection: { model: draftModelId, reasoning_effort: stop.value },
                });
              }}
              onResetToDefault={canResetEffort ? resetEffort : undefined}
              onHoverChange={setHoveredEffortIndex}
            />
            <span className={styles.effortReadout}>
              {hoveredEffort !== null ? (
                <span className={styles.effortPreview}>{hoveredEffort.label}</span>
              ) : selectedEffortIndex >= 0 ? (
                <>
                  <span className={styles.effortValue}>{effortStops[selectedEffortIndex].label}</span>
                  {canResetEffort && (
                    <Tooltip content="Reset to default" placement="above">
                      <button
                        type="button"
                        className={styles.effortResetIcon}
                        aria-label="Reset effort to Default"
                        onClick={resetEffort}
                      >
                        <Icon name="reset" size="sm" />
                      </button>
                    </Tooltip>
                  )}
                </>
              ) : unavailableEffort !== null ? (
                <>
                  <span className={styles.effortUnavailable}>{unavailableEffort}</span>
                  {canResetEffort && (
                    <Tooltip content="Reset to default" placement="above">
                      <button
                        type="button"
                        className={styles.effortResetIcon}
                        aria-label="Reset effort to Default"
                        onClick={resetEffort}
                      >
                        <Icon name="reset" size="sm" />
                      </button>
                    </Tooltip>
                  )}
                </>
              ) : defaultEffortIndex >= 0 ? (
                <>
                  <span className={styles.effortInherited}>{effortStops[defaultEffortIndex].label}</span>
                  <span className={styles.effortDefaultHint}>default</span>
                </>
              ) : (
                <span className={styles.effortMuted}>{hasEffortChoices ? FOLLOW_DEFAULT_LABEL : 'N/A'}</span>
              )}
            </span>
          </div>
        </div>
      )}
      <div className={styles.pickerActions}>
        {appliesNextTurn && <div className={styles.settingNote} role="note"><Text type="supporting">{PERMISSION_NOTE}</Text></div>}
        <Button
          label="Cancel"
          variant="ghost"
          size="sm"
          onClick={() => {
            setOpen(false);
          }}
        />
        <Button
          label="Save"
          variant="primary"
          size="sm"
          isDisabled={draftBlocked}
          onClick={() => {
            onChange(draft.selection, draft.provider);
            setOpen(false);
          }}
        />
      </div>
    </div>
  );

  return (
    <HStack gap={1} align="center" wrap="wrap" className={styles.group}>
      <span ref={hostRef} className={styles.host} onKeyDownCapture={onEscape}>
        <Popover
          placement={placement}
          alignment="start"
          width="min(23rem, calc(100vw - 1.5rem))"
          label="Select model and reasoning effort"
          hasCloseButton={false}
          className={styles.compactPopover}
          isOpen={open && !triggerDisabled}
          onOpenChange={(next) => { handleOpenChange(next && !triggerDisabled); }}
          isEnabled={!triggerDisabled}
          content={popoverContent}
        >
          <Button
            id={triggerId}
            label={spokenLabel}
            variant={effortControl === 'in-menu' ? 'secondary' : 'ghost'}
            size="sm"
            isDisabled={triggerDisabled}
            className={styles.trigger}
          >
            <Text key={label} type="inherit" color="inherit" maxLines={1} hasTruncateTooltip={!open}>
              {label}
            </Text>
          </Button>
        </Popover>
      </span>
    </HStack>
  );
}

function modelSearchScore<Provider extends string>(
  group: ModelGroup<Provider>,
  model: CatalogEntry,
  query: string,
): number {
  const fullProvider = providerLabel(group, group.provider);
  const shortProvider = shortProviderLabel(group, group.provider, fullProvider);
  return scoreModelSearch(
    [model.display_name, model.model, model.resolved_model, shortProvider, fullProvider, String(group.provider)],
    query,
  );
}

function defaultRowLabel<Provider extends string>(group: ModelGroup<Provider>): ReactNode {
  const defaultName = defaultNameOf(group.catalog);
  if (group.catalog?.default_source === 'acp_session') {
    return <ResolvedLabel name="Session settings" resolved={defaultName ?? 'current'} />;
  }
  return defaultName === null
    ? FOLLOW_DEFAULT_LABEL
    : <ResolvedLabel name={FOLLOW_DEFAULT_LABEL} resolved={defaultName} />;
}

function modelRowLabel(model: CatalogEntry): ReactNode {
  return model.resolved_model === null
    ? model.display_name
    : <ResolvedLabel name={model.display_name} resolved={model.resolved_model} />;
}

/** All providers combined into one model-centric list with right-aligned provider badges. */
function UnifiedModelList<Provider extends string>({
  groups, activeGroup, draft, query, onSelect,
}: Readonly<{
  groups: readonly ModelGroup<Provider>[];
  activeGroup: ModelGroup<Provider>;
  draft: Readonly<{ provider: Provider; selection: ModelSelection }>;
  query: string;
  onSelect: (provider: Provider, model: string | null) => void;
}>) {
  const orderedGroups = [activeGroup, ...groups.filter((group) => group.provider !== activeGroup.provider)];
  const activeDefaultName = defaultNameOf(activeGroup.catalog);
  const activeReason = catalogReason(activeGroup);
  const activeBadge = shortProviderLabel(activeGroup, activeGroup.provider);
  const showDefaultRow = query === '' || scoreModelSearch([FOLLOW_DEFAULT_LABEL, activeDefaultName, activeBadge, String(activeGroup.provider)], query) > 0;

  const entries: { group: ModelGroup<Provider>; model: CatalogEntry; score: number }[] = [];
  const noticeGroups: ModelGroup<Provider>[] = [];
  for (const group of orderedGroups) {
    const info = catalogReason(group);
    const blocked = info.blocked !== null;
    const providerMatchesQuery = query !== '' && scoreModelSearch(
      [shortProviderLabel(group, group.provider), providerLabel(group, group.provider), String(group.provider)],
      query,
    ) > 0;
    const hasNotice = (group.selectionProblem !== undefined && group.availability?.authentication_notice != null)
      || info.reason !== null;
    if (hasNotice && (group.provider === activeGroup.provider || !blocked || providerMatchesQuery)) {
      noticeGroups.push(group);
    }
    if (blocked) continue;
    for (const model of group.catalog?.models ?? []) {
      const score = modelSearchScore(group, model, query);
      if (score > 0) entries.push({ group, model, score });
    }
  }
  if (query !== '') {
    entries.sort((left, right) => right.score - left.score);
  }

  // Group identical unavailable notices in `All` view so shared server issues (e.g. missing bridge) render once concisely.
  const groupedReasons: {
    key: string;
    labels: string[];
    reason: string;
    refused: boolean;
  }[] = [];
  const authNotices: { key: string; text: string }[] = [];
  for (const group of noticeGroups) {
    if (group.selectionProblem !== undefined && group.availability?.authentication_notice != null) {
      authNotices.push({ key: `auth:${group.provider}`, text: group.availability.authentication_notice.text });
    }
    const { reason, refused } = catalogReason(group);
    if (reason === null) continue;
    const label = providerLabel(group, group.provider);
    const existing = groupedReasons.find((entry) => entry.reason === reason && entry.refused === refused);
    if (existing !== undefined) {
      existing.labels.push(label);
    } else {
      groupedReasons.push({ key: `reason:${group.provider}`, labels: [label], reason, refused });
    }
  }

  return <>
    {authNotices.map((notice) => (
      <div key={notice.key} className={styles.reason} role="note">
        <Text type="supporting">{notice.text}</Text>
      </div>
    ))}
    {groupedReasons.map(({ key, labels, reason, refused }) => {
      const subject = labels.length === 1
        ? `${labels[0]} is unavailable`
        : `${labels.join(', ')} are unavailable`;
      const suffix = refused ? '' : ` ${unavailableCreationNote(labels.length === 1 ? labels[0] : 'the provider')}`;
      return (
        <div key={key} className={refused ? styles.reason : `${styles.reason} ${styles.warning}`} role="note">
          <Text type="supporting" color={refused ? undefined : 'inherit'}>
            {`${subject}: ${reason}${suffix}`}
          </Text>
        </div>
      );
    })}
    {showDefaultRow && (
      <>
        <ModelRow
          label={defaultRowLabel(activeGroup)}
          providerTag={activeBadge}
          isSelected={draft.provider === activeGroup.provider && draft.selection.model === null}
          isDisabled={activeReason.blocked !== null || activeGroup.defaultAvailable === false}
          onSelect={() => onSelect(activeGroup.provider, null)}
        />
        {entries.length > 0 && <div className={styles.defaultDivider} aria-hidden="true" />}
      </>
    )}
    {entries.map(({ group, model }) => (
      <ModelRow
        key={`${group.provider}:${model.id}`}
        label={modelRowLabel(model)}
        providerTag={shortProviderLabel(group, group.provider)}
        isSelected={draft.provider === group.provider && draft.selection.model === model.model}
        onSelect={() => onSelect(group.provider, model.model)}
      />
    ))}
    {entries.length === 0 && !showDefaultRow && noticeGroups.length === 0 && (
      <div className={styles.pickerEmpty}>No matching models</div>
    )}
  </>;
}

/** One group's rows: Default, then the catalog. `selection` is `null` when the pick lies in another group. */
function GroupChoices<Provider extends string>({ group, selection, query = '', onClearFilter, onChange }: Readonly<{
  group: ModelGroup<Provider>;
  selection: ModelSelection | null;
  query?: string;
  onClearFilter?: () => void;
  onChange: (model: string | null) => void;
}>) {
  const allModels: readonly CatalogEntry[] = group.catalog?.models ?? [];
  const models = query === ''
    ? allModels
    : allModels
      .map((model) => ({ model, score: modelSearchScore(group, model, query) }))
      .filter((entry) => entry.score > 0)
      .sort((left, right) => right.score - left.score)
      .map((entry) => entry.model);
  const defaultName = defaultNameOf(group.catalog);
  const unreachable = group.catalog !== null && group.catalog.source === 'unavailable';
  const { reason, refused, blocked } = catalogReason(group);
  const showDefault = query === '' || scoreModelSearch([FOLLOW_DEFAULT_LABEL, defaultName], query) > 0;

  return <>
    {group.selectionProblem !== undefined && group.availability?.authentication_notice != null && (
      <div className={styles.reason} role="note"><Text type="supporting">{group.availability.authentication_notice.text}</Text></div>
    )}
    {reason !== null && (
      <div className={refused ? styles.reason : `${styles.reason} ${styles.warning}`} role="note">
        <Text type="supporting" color={refused ? undefined : 'inherit'}>
          {`${providerLabel(group, group.provider)} is unavailable: ${reason}${refused ? '' : ` ${unavailableCreationNote(providerLabel(group, group.provider))}`}`}
        </Text>
      </div>
    )}
    {showDefault && (
      <>
        <ModelRow
          label={defaultRowLabel(group)}
          isSelected={selection !== null && selection.model === null}
          isDisabled={blocked !== null || group.defaultAvailable === false}
          onSelect={() => onChange(null)}
        />
        {models.length > 0 && <div className={styles.defaultDivider} aria-hidden="true" />}
      </>
    )}
    {models.map((model) => (
      <ModelRow
        key={model.id}
        label={modelRowLabel(model)}
        isSelected={selection !== null && selection.model === model.model}
        isDisabled={blocked !== null}
        onSelect={() => onChange(model.model)}
      />
    ))}
    {models.length === 0 && !showDefault && (
      <div className={styles.pickerEmpty}>
        <span>No matching models</span>
        {onClearFilter !== undefined && (
          <button type="button" className={styles.emptyAction} onClick={onClearFilter}>Search all providers</button>
        )}
      </div>
    )}
    {allModels.length === 0 && reason === null && (
      <div className={styles.pickerEmpty}>
        {unreachable ? (group.selectionProblem !== undefined ? `${group.provider} model catalog is unavailable`
          : providerPresentation(group.provider)?.unavailable ?? `${group.provider} is unavailable`) : 'No models available on this account'}
      </div>
    )}
  </>;
}

function ModelRow({
  label, providerTag, isSelected, isDisabled = false, onSelect,
}: Readonly<{
  label: ReactNode;
  providerTag?: string;
  isSelected: boolean;
  isDisabled?: boolean;
  onSelect: () => void;
}>) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={isSelected}
      disabled={isDisabled}
      className={styles.pickerRow}
      onClick={onSelect}
    >
      <span className={styles.pickerRowLabel}>{label}</span>
      {providerTag !== undefined && <span className={styles.providerBadge}>{providerTag}</span>}
      {isSelected && (
        <>
          <AstryxIcon icon="check" size="sm" color="accent" className={styles.pickerCheck} />
          <VisuallyHidden>Selected</VisuallyHidden>
        </>
      )}
    </button>
  );
}

/**
 * One line (owner layout, 2026-09-28): the entry's name, and the model it resolves to small and quiet at the
 * right, which gives way with an ellipsis first when the menu runs out of room.
 */
function ResolvedLabel({ name, resolved }: Readonly<{ name: string; resolved: string }>) {
  return (
    <span className={styles.resolvedRow}>
      <span className={styles.resolvedName}>{name}</span>
      <span className={styles.resolvedId} title={resolved}>{resolved}</span>
    </span>
  );
}
