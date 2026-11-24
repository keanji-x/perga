// Codex-style drill-down model & reasoning-effort picker for the composer footer and new-track page.
// Presentational: every value is a prop; queries and writes live in `app/router`.
// Level 1 shows the active model entry (`>`) above an instant-apply reasoning-effort slider.
// Level 2 drills into a compact master-detail provider rail + model list with global cross-provider search.
// Every selection applies immediately on click/slide — zero Save/Cancel form chrome.

import { Button } from '@astryxdesign/core/Button';
import { HStack } from '@astryxdesign/core/HStack';
import { Icon as AstryxIcon } from '@astryxdesign/core/Icon';
import { Popover } from '@astryxdesign/core/Popover';
import { Text } from '@astryxdesign/core/Text';
import { VisuallyHidden } from '@astryxdesign/core/VisuallyHidden';

import { useEffect, useRef, type KeyboardEvent, type ReactNode, type RefObject } from 'react';

import { unavailableCreationNote } from '../../../../../core/domain/agent-providers.ts';
import type { ModelSelection } from '../../../../../core/domain/conversation.ts';
import type { AgentProvider } from '../../../../../core/domain/presentation-types.ts';
import { providerPresentation } from '../../../../../core/domain/provider-presentation.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import {
  catalogReason,
  defaultNameOf,
  effortsFor,
  FOLLOW_DEFAULT_LABEL,
  providerLabel,
  scoreModelSearch,
  shortProviderLabel,
  visibleModelGroups,
  type CatalogEntry,
  type ModelGroup,
} from './model-selection.ts';
import styles from './model-pill.module.css';
import { PERMISSION_NOTE, PermissionModePill, useEscapeFocus } from './setting-pill.tsx';

export { PermissionModePill };

type PickerView = 'overview' | 'models';

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
  /** Compact mobile hint (`'in-menu'` uses the secondary pill trigger without chevron). */
  effortControl?: 'separate' | 'in-menu';
  appliesNextTurn?: boolean;
}>) {
  const [open, setOpen] = useState(false);
  const [view, setView] = useState<PickerView>('overview');
  const [searchQuery, setSearchQuery] = useState('');
  const [viewedProvider, setViewedProvider] = useState<Provider>(provider);
  const [pendingSelection, setPendingSelection] = useState<Readonly<{
    provider: Provider;
    selection: ModelSelection;
    baseProvider: Provider;
    baseSelection: ModelSelection;
  }> | null>(null);
  const hostRef = useRef<HTMLSpanElement | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);
  const searchInputRef = useRef<HTMLInputElement | null>(null);
  const backButtonRef = useRef<HTMLButtonElement | null>(null);
  const summaryButtonRef = useRef<HTMLButtonElement | null>(null);
  const sliderFocusRef = useRef<HTMLDivElement | null>(null);
  const pendingFocusRef = useRef<'models' | 'overview-model' | 'overview-slider' | 'panel' | null>(null);
  const { onEscape, onOpenChange } = useEscapeFocus(open, setOpen, hostRef);

  // Use optimistic in-popover selection only while server-backed props still equal the pre-write snapshot;
  // any prop update from the caller immediately supersedes and clears the optimistic entry.
  const propsUnchangedSinceWrite = pendingSelection !== null
    && pendingSelection.baseProvider === provider
    && pendingSelection.baseSelection.model === selection.model
    && pendingSelection.baseSelection.reasoning_effort === selection.reasoning_effort;
  const effectivePending = open && propsUnchangedSinceWrite ? pendingSelection : null;
  const activeProvider = effectivePending?.provider ?? provider;
  const activeSelection = effectivePending?.selection ?? selection;

  useEffect(() => {
    if (pendingSelection !== null && (!open || !propsUnchangedSinceWrite)) {
      setPendingSelection(null);
    }
  }, [open, pendingSelection, propsUnchangedSinceWrite]);

  const isCompactTrigger = effortControl === 'in-menu';
  const shown = visibleModelGroups(groups, activeProvider);
  const hasMultipleProviders = shown.length > 1;
  const grouped = hasMultipleProviders || shown.some((group) => group.selectionProblem !== undefined);
  const activeGroup = groups.find((group) => group.provider === activeProvider);
  const catalog = activeGroup?.catalog ?? null;
  const models = catalog?.models ?? [];
  const unreachable = shown.length > 0
    && shown.every((group) => group.catalog !== null && group.catalog.source === 'unavailable');
  const defaultName = defaultNameOf(catalog);
  const chosen = activeSelection.model === null
    ? undefined
    : models.find((model) => model.model === activeSelection.model);
  const defaultEntry = defaultName === null
    ? undefined
    : models.find((model) => model.model === defaultName || model.resolved_model === defaultName);
  const named = activeSelection.model === null
    ? (defaultEntry?.display_name ?? defaultName ?? FOLLOW_DEFAULT_LABEL)
    : (chosen?.display_name ?? activeSelection.model);

  const { efforts, effortDefault } = effortsFor(activeGroup, activeSelection.model);
  const activeReason = activeGroup ? catalogReason(activeGroup) : { reason: null, refused: false, blocked: null };
  const activeUnavailable = activeGroup === undefined
    || activeReason.blocked !== null
    || activeGroup.availability?.status === 'unavailable'
    || activeGroup.catalog?.source === 'unavailable';
  const advertisedDefault = effortDefault !== null
    && efforts.some((entry) => entry.reasoning_effort === effortDefault)
    ? effortDefault
    : null;
  const unavailableEffort = activeSelection.reasoning_effort !== null
    && !efforts.some((entry) => entry.reasoning_effort === activeSelection.reasoning_effort)
    ? activeSelection.reasoning_effort
    : null;
  const hasEffortControl = (efforts.length > 0 && !activeUnavailable)
    || (unavailableEffort !== null && activeReason.blocked === null);

  const activeEffortLabel = activeSelection.reasoning_effort ?? advertisedDefault;
  const activeProviderName = providerLabel(activeGroup, activeProvider);
  const baseLabel = grouped && !named.toLowerCase().startsWith(activeProviderName.toLowerCase())
    ? `${activeProviderName} ${named}`
    : named;
  const label = hasEffortControl && activeEffortLabel !== null
    ? `${baseLabel} · ${activeEffortLabel}`
    : baseLabel;

  const spokenLabel = activeSelection.model === null && defaultName !== null
    ? `Model: ${label} (this installation's default)`
    : `Model: ${label}`;

  const triggerDisabled = isDisabled
    || (unreachable && !shown.some((group) => group.selectionProblem === null || group.acceptsUnavailableBlankTrack === true));

  const effectiveView: PickerView = hasEffortControl ? view : 'models';

  useEffect(() => {
    if (!open) return;
    const target = pendingFocusRef.current;
    if (target === null) return;
    pendingFocusRef.current = null;
    if (target === 'models') {
      const next = searchInputRef.current
        ?? backButtonRef.current
        ?? panelRef.current?.querySelector<HTMLElement>('[role="radio"]:not(:disabled)');
      next?.focus();
    } else if (target === 'overview-model') {
      summaryButtonRef.current?.focus();
    } else if (target === 'overview-slider') {
      (sliderFocusRef.current ?? summaryButtonRef.current)?.focus();
    } else {
      panelRef.current?.focus();
    }
  }, [open, effectiveView]);

  const handleOpenChange = (nextOpen: boolean) => {
    setSearchQuery('');
    pendingFocusRef.current = null;
    if (nextOpen) {
      setPendingSelection(null);
      setViewedProvider(provider);
      setView(hasEffortControl ? 'overview' : 'models');
    } else {
      setPendingSelection(null);
    }
    onOpenChange(nextOpen);
  };

  const totalModelCount = shown.reduce((sum, group) => sum + (group.catalog?.models.length ?? 0), 0);
  const showSearch = totalModelCount > 6;
  const normalizedQuery = searchQuery.trim().toLowerCase();
  const isSearching = normalizedQuery !== '';

  const pickModel = (targetProvider: Provider, model: string | null, viaKeyboard = false) => {
    const targetGroup = shown.find((group) => group.provider === targetProvider);
    const targetReason = targetGroup ? catalogReason(targetGroup) : { reason: null, refused: false, blocked: null };
    const targetUnavailable = targetGroup === undefined
      || targetReason.blocked !== null
      || targetGroup.availability?.status === 'unavailable'
      || targetGroup.catalog?.source === 'unavailable';
    const targetHasEfforts = !targetUnavailable && effortsFor(targetGroup, model).efforts.length > 0;

    if (targetProvider !== provider || model !== selection.model) {
      const nextSelection: ModelSelection = { model, reasoning_effort: null };
      if (targetHasEfforts) {
        setPendingSelection({
          provider: targetProvider,
          selection: nextSelection,
          baseProvider: provider,
          baseSelection: selection,
        });
      } else {
        setPendingSelection(null);
      }
      onChange(nextSelection, targetProvider);
    } else if (effectivePending !== null) {
      setPendingSelection(null);
    }

    setSearchQuery('');
    if (targetHasEfforts) {
      setViewedProvider(targetProvider);
      pendingFocusRef.current = viaKeyboard ? 'overview-slider' : 'panel';
      setView('overview');
    } else {
      setOpen(false);
    }
  };

  const pickEffort = (nextEffort: string | null) => {
    const isUnchangedCommitted = effectivePending === null
      && nextEffort === selection.reasoning_effort;
    if (isUnchangedCommitted) return;
    if (nextEffort === null && efforts.length === 0) {
      pendingFocusRef.current = 'models';
    }
    const nextSelection: ModelSelection = { model: activeSelection.model, reasoning_effort: nextEffort };
    if (effectivePending !== null) {
      setPendingSelection({
        provider: activeProvider,
        selection: nextSelection,
        baseProvider: provider,
        baseSelection: selection,
      });
    }
    onChange(nextSelection, activeProvider);
  };

  const allBlocked = shown.length > 0 && shown.every((group) => catalogReason(group).blocked !== null);
  const showSplitRail = hasMultipleProviders && !isSearching && !allBlocked;
  const viewedGroup = shown.find((group) => group.provider === viewedProvider)
    ?? shown.find((group) => group.provider === activeProvider)
    ?? shown[0];

  const popoverWidth = effectiveView === 'overview'
    ? 'min(17.5rem, calc(100vw - 1.5rem))'
    : hasMultipleProviders
      ? 'min(26.5rem, calc(100vw - 1.5rem))'
      : 'min(21rem, calc(100vw - 1.5rem))';

  const popoverContent = shown.length === 0 ? null : (
    <div
      ref={panelRef}
      tabIndex={-1}
      className={styles.pickerPanel}
      role="presentation"
      /* Stop React synthetic events from bubbling into a host ChatComposer, whose click handler yanks focus back to the message editor. */
      onMouseDown={(event) => event.stopPropagation()}
      onClick={(event) => event.stopPropagation()}
    >
      {effectiveView === 'overview' ? (
        <OverviewPane
          modelLabel={named}
          providerName={grouped ? activeProviderName : null}
          efforts={efforts}
          advertisedDefault={advertisedDefault}
          selectedEffort={activeSelection.reasoning_effort}
          unavailableEffort={unavailableEffort}
          isDisabled={isDisabled || activeReason.blocked !== null}
          summaryButtonRef={summaryButtonRef}
          sliderFocusRef={sliderFocusRef}
          onOpenModels={() => {
            setViewedProvider(activeProvider);
            setSearchQuery('');
            pendingFocusRef.current = 'models';
            setView('models');
          }}
          onChangeEffort={pickEffort}
        />
      ) : (
        <>
          {(hasEffortControl || showSearch) && (
            <div className={styles.modelsHeader}>
              {hasEffortControl && (
                <button
                  ref={backButtonRef}
                  type="button"
                  className={styles.backButton}
                  aria-label="Back to model settings"
                  onClick={() => {
                    setSearchQuery('');
                    pendingFocusRef.current = 'overview-model';
                    setView('overview');
                  }}
                >
                  <Icon name="chevron-left" size="sm" />
                </button>
              )}
              {showSearch ? (
                <div className={styles.searchBarInline}>
                  <label className={styles.searchField}>
                    <span className={styles.searchIcon} aria-hidden="true"><Icon name="search" size="sm" /></span>
                    <input
                      ref={searchInputRef}
                      type="search"
                      className={styles.searchInput}
                      value={searchQuery}
                      aria-label="Filter models"
                      placeholder={hasMultipleProviders ? 'Search all models…' : 'Search models…'}
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
              ) : (
                <span className={styles.modelsHeaderTitle}>Select model</span>
              )}
            </div>
          )}

          {allBlocked ? (
            <div className={styles.modelList} role="group" aria-label="Models">
              <UnavailableNoticeList groups={shown} />
            </div>
          ) : showSplitRail && viewedGroup !== undefined ? (
            <div className={styles.splitBody}>
              <div className={styles.providerRail} role="group" aria-label="Providers">
                {shown.map((group) => {
                  const isViewed = group.provider === viewedGroup.provider;
                  const isCurrent = group.provider === activeProvider;
                  const isUnavailable = catalogReason(group).blocked !== null
                    || group.availability?.status === 'unavailable';
                  const labelText = providerLabel(group, group.provider);
                  return (
                    <button
                      key={group.provider}
                      type="button"
                      role="radio"
                      aria-checked={isViewed}
                      data-nc-active={isViewed ? 'true' : 'false'}
                      data-nc-unavailable={isUnavailable ? 'true' : 'false'}
                      className={styles.providerItem}
                      onClick={() => setViewedProvider(group.provider)}
                    >
                      <span className={styles.providerItemLabel}>{labelText}</span>
                      {isUnavailable ? (
                        <span className={styles.providerDotMuted} aria-hidden="true" />
                      ) : isCurrent ? (
                        <span className={styles.providerDotCurrent} aria-hidden="true" />
                      ) : null}
                    </button>
                  );
                })}
              </div>
              <div className={styles.modelPane} role="group" aria-label="Models">
                <SingleGroupPane
                  group={viewedGroup}
                  activeProvider={activeProvider}
                  selection={activeSelection}
                  onPick={pickModel}
                />
              </div>
            </div>
          ) : isSearching ? (
            <SearchResultsList
              groups={shown}
              activeProvider={activeProvider}
              selection={activeSelection}
              query={normalizedQuery}
              onPick={pickModel}
            />
          ) : shown[0] !== undefined ? (
            <div className={styles.modelList} role="group" aria-label="Models">
              <SingleGroupPane
                group={shown[0]}
                activeProvider={activeProvider}
                selection={activeSelection}
                onPick={pickModel}
              />
            </div>
          ) : null}
        </>
      )}

      {appliesNextTurn && (
        <div className={styles.pickerNoteFooter}>
          <div className={styles.settingNote} role="note">
            <Text type="supporting">{PERMISSION_NOTE}</Text>
          </div>
        </div>
      )}
    </div>
  );

  return (
    <HStack gap={1} align="center" wrap="wrap" className={styles.group}>
      <span ref={hostRef} className={styles.host} onKeyDownCapture={onEscape}>
        <Popover
          placement={placement}
          alignment="start"
          width={popoverWidth}
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
            variant={isCompactTrigger ? 'secondary' : 'ghost'}
            size="sm"
            isDisabled={triggerDisabled}
            data-nc-compact={isCompactTrigger ? 'true' : undefined}
            className={styles.trigger}
          >
            <span className={styles.triggerInner}>
              <Text key={label} type="inherit" color="inherit" maxLines={1} hasTruncateTooltip={!open}>
                {label}
              </Text>
              {!isCompactTrigger && !triggerDisabled && (
                <AstryxIcon
                  icon="chevronDown"
                  size="xsm"
                  color="inherit"
                  className={styles.triggerChevron}
                />
              )}
            </span>
          </Button>
        </Popover>
      </span>
    </HStack>
  );
}

/** Compact 2-row overview: Model · Effort > on top, pure tick slider right underneath. */
function OverviewPane({
  modelLabel, providerName, efforts, advertisedDefault, selectedEffort, unavailableEffort, isDisabled, summaryButtonRef, sliderFocusRef, onOpenModels, onChangeEffort,
}: Readonly<{
  modelLabel: string;
  providerName: string | null;
  efforts: CatalogEntry['supported_reasoning_efforts'];
  advertisedDefault: string | null;
  selectedEffort: string | null;
  unavailableEffort: string | null;
  isDisabled: boolean;
  summaryButtonRef: RefObject<HTMLButtonElement | null>;
  sliderFocusRef: RefObject<HTMLDivElement | null>;
  onOpenModels: () => void;
  onChangeEffort: (effort: string | null) => void;
}>) {
  const [dragRatio, setDragRatio] = useState<number | null>(null);
  const activePointerIdRef = useRef<number | null>(null);
  const trackRef = useRef<HTMLDivElement | null>(null);

  // Never inject a synthetic "Default" tick onto the slider: every tick corresponds 1:1 to a real
  // server-advertised effort level. When the current selection is unset (`null`) and the model does
  // not declare which level is its default, the slider renders neutrally until the user picks one.
  const stops: readonly { value: string; label: string }[] = efforts.map((entry) => ({
    value: entry.reasoning_effort,
    label: entry.reasoning_effort,
  }));

  const effectiveValue = unavailableEffort !== null
    ? null
    : selectedEffort ?? advertisedDefault;
  const committedIndex = effectiveValue === null
    ? -1
    : stops.findIndex((stop) => stop.value === effectiveValue);
  const hasCommittedStop = committedIndex >= 0;
  const maxIndex = Math.max(0, stops.length - 1);
  const isDragging = dragRatio !== null;
  const activeIndex = isDragging && maxIndex > 0
    ? Math.round(dragRatio * maxIndex)
    : committedIndex;
  const showThumb = isDragging || hasCommittedStop;
  const ratio = isDragging
    ? dragRatio
    : (hasCommittedStop && maxIndex > 0 ? committedIndex / maxIndex : 0);
  // Inset stop centers by half the 6px rail height (3px) so end ticks sit concentrically inside the rounded caps.
  const railPosition = (r: number) => `calc(3px + (100% - 6px) * ${r})`;
  const railFillWidth = showThumb ? `calc(6px + (100% - 6px) * ${ratio})` : '0px';

  const previewStop = activeIndex >= 0 ? stops.at(activeIndex) : undefined;
  const valueText = isDragging
    ? (previewStop?.label ?? FOLLOW_DEFAULT_LABEL)
    : unavailableEffort !== null
      ? `${unavailableEffort} (Unavailable)`
      : selectedEffort !== null
        ? selectedEffort
        : advertisedDefault !== null
          ? `${FOLLOW_DEFAULT_LABEL} (${advertisedDefault})`
          : FOLLOW_DEFAULT_LABEL;

  const readoutText = isDragging
    ? (previewStop?.label ?? FOLLOW_DEFAULT_LABEL)
    : unavailableEffort !== null
      ? unavailableEffort
      : (selectedEffort ?? advertisedDefault ?? FOLLOW_DEFAULT_LABEL);

  const ratioFromClientX = (clientX: number): number => {
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect || rect.width <= 6) return 0;
    return Math.min(1, Math.max(0, (clientX - (rect.left + 3)) / (rect.width - 6)));
  };

  const handleSliderKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (isDisabled || stops.length < 2) return;
    if (event.key === 'Backspace' || event.key === 'Delete') {
      if (selectedEffort !== null) {
        event.preventDefault();
        onChangeEffort(null);
      }
      return;
    }
    let nextIndex: number | null = null;
    if (event.key === 'ArrowRight' || event.key === 'ArrowUp') {
      nextIndex = hasCommittedStop ? Math.min(maxIndex, committedIndex + 1) : 0;
    } else if (event.key === 'ArrowLeft' || event.key === 'ArrowDown') {
      nextIndex = hasCommittedStop ? Math.max(0, committedIndex - 1) : 0;
    } else if (event.key === 'Home') {
      nextIndex = 0;
    } else if (event.key === 'End') {
      nextIndex = maxIndex;
    }
    if (nextIndex === null) return;
    event.preventDefault();
    const nextStop = stops.at(nextIndex);
    if (nextStop) onChangeEffort(nextStop.value);
  };

  return (
    <div className={styles.overviewPane}>
      <button
        ref={summaryButtonRef}
        type="button"
        className={styles.modelSummaryRow}
        aria-label={`Change model: ${providerName ? `${providerName} ${modelLabel}` : modelLabel}`}
        onClick={onOpenModels}
      >
        <span className={styles.modelSummaryMeta}>
          {providerName && <span className={styles.modelSummaryCaption}>{providerName}</span>}
          <span className={styles.modelSummaryLine}>
            <span className={styles.modelSummaryName}>{modelLabel}</span>
            <span className={styles.modelSummaryDot} aria-hidden="true">·</span>
            <span
              className={unavailableEffort !== null && !isDragging
                ? styles.effortReadoutWarning
                : styles.effortReadoutInline}
            >
              {readoutText}
            </span>
          </span>
        </span>
        <span className={styles.modelSummaryChevron} aria-hidden="true">
          <Icon name="chevron-right" size="sm" />
        </span>
      </button>

      {stops.length >= 2 ? (
        <div className={styles.sliderRow}>
          <div
            ref={(node) => {
              trackRef.current = node;
              sliderFocusRef.current = node;
            }}
            role="slider"
            tabIndex={isDisabled ? -1 : 0}
            aria-label="Reasoning effort"
            aria-valuemin={0}
            aria-valuemax={maxIndex}
            aria-valuenow={hasCommittedStop || isDragging ? activeIndex : 0}
            aria-valuetext={valueText}
            aria-disabled={isDisabled || undefined}
            data-nc-dragging={isDragging ? 'true' : 'false'}
            data-nc-has-thumb={showThumb ? 'true' : 'false'}
            className={styles.sliderTrackShell}
            onKeyDown={handleSliderKeyDown}
            onPointerDown={(event) => {
              if (isDisabled || event.button !== 0 || activePointerIdRef.current !== null) return;
              event.preventDefault();
              activePointerIdRef.current = event.pointerId;
              event.currentTarget.setPointerCapture?.(event.pointerId);
              setDragRatio(ratioFromClientX(event.clientX));
            }}
            onPointerMove={(event) => {
              if (activePointerIdRef.current !== event.pointerId || isDisabled) return;
              setDragRatio(ratioFromClientX(event.clientX));
            }}
            onPointerUp={(event) => {
              if (activePointerIdRef.current !== event.pointerId) return;
              const finalRatio = ratioFromClientX(event.clientX);
              const snappedIndex = maxIndex > 0 ? Math.round(finalRatio * maxIndex) : 0;
              activePointerIdRef.current = null;
              if (event.currentTarget.hasPointerCapture?.(event.pointerId)) {
                event.currentTarget.releasePointerCapture(event.pointerId);
              }
              setDragRatio(null);
              const nextStop = stops.at(snappedIndex);
              if (nextStop) onChangeEffort(nextStop.value);
            }}
            onPointerCancel={(event) => {
              if (activePointerIdRef.current !== event.pointerId) return;
              activePointerIdRef.current = null;
              setDragRatio(null);
            }}
          >
            <div className={styles.sliderTrackRail} aria-hidden="true">
              <div className={styles.sliderTrackFill} style={{ width: railFillWidth }} />
              {stops.map((stop, index) => {
                const tickRatio = maxIndex > 0 ? index / maxIndex : 0;
                const isFilled = showThumb && tickRatio <= ratio + 0.005;
                return (
                  <span
                    key={stop.value}
                    className={styles.sliderTick}
                    data-nc-filled={isFilled ? 'true' : 'false'}
                    style={{ left: railPosition(tickRatio) }}
                  />
                );
              })}
            </div>
            {showThumb && (
              <span
                className={styles.sliderThumb}
                style={{ left: railPosition(ratio) }}
                aria-hidden="true"
              />
            )}
          </div>
        </div>
      ) : stops.length === 1 ? (
        <div className={styles.singleEffortRow} role="radiogroup" aria-label="Reasoning effort">
          <button
            type="button"
            role="radio"
            aria-checked={selectedEffort === null}
            disabled={isDisabled}
            className={styles.singleEffortOption}
            onClick={() => onChangeEffort(null)}
          >
            {FOLLOW_DEFAULT_LABEL}
          </button>
          <button
            type="button"
            role="radio"
            aria-checked={selectedEffort === stops[0].value}
            disabled={isDisabled}
            className={styles.singleEffortOption}
            onClick={() => onChangeEffort(stops[0].value)}
          >
            {stops[0].label}
          </button>
        </div>
      ) : unavailableEffort !== null && !isDisabled ? (
        <div className={styles.sliderRow}>
          <button
            type="button"
            className={styles.effortClearButton}
            aria-label="Clear unavailable effort"
            onClick={() => onChangeEffort(null)}
          >
            Use default
          </button>
        </div>
      ) : null}
    </div>
  );
}

/** One provider's model list (or unavailable callout when blocked or empty & unavailable). */
function SingleGroupPane<Provider extends string>({
  group, activeProvider, selection, onPick,
}: Readonly<{
  group: ModelGroup<Provider>;
  activeProvider: Provider;
  selection: ModelSelection;
  onPick: (targetProvider: Provider, model: string | null, viaKeyboard?: boolean) => void;
}>) {
  const { blocked } = catalogReason(group);
  if (blocked !== null) {
    return <UnavailableNoticeList groups={[group]} />;
  }
  const section = buildGroupSection(group, activeProvider, selection, '');
  const unavailableEmpty = group.availability?.status === 'unavailable' && section.models.length === 0;
  const fullProvider = providerLabel(group, group.provider);

  return (
    <>
      {section.authNotice !== null && (
        <div className={styles.reason} role="note">
          <Text type="supporting">{section.authNotice}</Text>
        </div>
      )}
      {section.warningNotice !== null && (
        <div className={`${styles.reason} ${styles.warning}`} role="note">
          <Text type="supporting" color="inherit">{section.warningNotice}</Text>
        </div>
      )}
      {unavailableEmpty ? (
        group.acceptsUnavailableBlankTrack === true && group.defaultAvailable !== false && (
          <button
            type="button"
            className={styles.unavailableUseButton}
            disabled={group.provider === activeProvider && selection.model === null}
            onClick={(event) => onPick(group.provider, null, event.detail === 0)}
          >
            {group.provider === activeProvider && selection.model === null
              ? `Using ${fullProvider} when available`
              : `Use ${fullProvider} anyway`}
          </button>
        )
      ) : (
        <>
          {section.showDefault && (
            <ModelRow
              label={defaultRowLabel(group)}
              isSelected={group.provider === activeProvider && selection.model === null}
              isDisabled={group.defaultAvailable === false}
              onSelect={(viaKeyboard) => onPick(group.provider, null, viaKeyboard)}
            />
          )}
          {section.models.map((model) => (
            <ModelRow
              key={`${group.provider}:${model.id}`}
              label={modelRowLabel(model)}
              isSelected={group.provider === activeProvider && selection.model === model.model}
              onSelect={(viaKeyboard) => onPick(group.provider, model.model, viaKeyboard)}
            />
          ))}
          {section.emptyCatalogMessage !== null && (
            <div className={styles.pickerEmpty}>{section.emptyCatalogMessage}</div>
          )}
        </>
      )}
    </>
  );
}

/** Cross-provider search results shown while the user types in the search box. */
function SearchResultsList<Provider extends string>({
  groups, activeProvider, selection, query, onPick,
}: Readonly<{
  groups: readonly ModelGroup<Provider>[];
  activeProvider: Provider;
  selection: ModelSelection;
  query: string;
  onPick: (targetProvider: Provider, model: string | null, viaKeyboard?: boolean) => void;
}>) {
  const showHeadings = groups.length > 1;
  const usableSections = groups
    .filter((group) => catalogReason(group).blocked === null)
    .map((group) => buildGroupSection(group, activeProvider, selection, query))
    .filter((section) => {
      const unavailableEmpty = section.group.availability?.status === 'unavailable' && section.models.length === 0;
      return !unavailableEmpty && (section.showDefault || section.models.length > 0);
    });
  const matchingUnavailable = groups.filter((group) =>
    (catalogReason(group).blocked !== null || (group.availability?.status === 'unavailable' && (group.catalog?.models.length ?? 0) === 0))
    && scoreModelSearch(
      [shortProviderLabel(group, group.provider), providerLabel(group, group.provider), String(group.provider)],
      query,
    ) > 0);
  const selectableUnavailable = matchingUnavailable.filter((group) =>
    catalogReason(group).blocked === null
    && group.acceptsUnavailableBlankTrack === true
    && group.defaultAvailable !== false);

  return (
    <div className={styles.modelList} role="group" aria-label="Models">
      {usableSections.map((section, index) => (
        <div key={section.group.provider} className={index > 0 ? `${styles.sectionGroup} ${styles.sectionSeparated}` : styles.sectionGroup}>
          {showHeadings && (
            <div className={styles.sectionHeading}>{providerLabel(section.group, section.group.provider)}</div>
          )}
          {section.showDefault && (
            <ModelRow
              label={defaultRowLabel(section.group)}
              isSelected={section.group.provider === activeProvider && selection.model === null}
              isDisabled={section.group.defaultAvailable === false}
              onSelect={(viaKeyboard) => onPick(section.group.provider, null, viaKeyboard)}
            />
          )}
          {section.models.map((model) => (
            <ModelRow
              key={`${section.group.provider}:${model.id}`}
              label={modelRowLabel(model)}
              isSelected={section.group.provider === activeProvider && selection.model === model.model}
              onSelect={(viaKeyboard) => onPick(section.group.provider, model.model, viaKeyboard)}
            />
          ))}
        </div>
      ))}
      {matchingUnavailable.length > 0 && (
        <div className={usableSections.length > 0 ? `${styles.sectionGroup} ${styles.sectionSeparated}` : styles.sectionGroup}>
          <UnavailableNoticeList groups={matchingUnavailable} />
          {selectableUnavailable.map((group) => {
            const fullProvider = providerLabel(group, group.provider);
            const isActive = group.provider === activeProvider && selection.model === null;
            return (
              <button
                key={`use:${group.provider}`}
                type="button"
                className={styles.unavailableUseButton}
                disabled={isActive}
                onClick={(event) => onPick(group.provider, null, event.detail === 0)}
              >
                {isActive ? `Using ${fullProvider} when available` : `Use ${fullProvider} anyway`}
              </button>
            );
          })}
        </div>
      )}
      {usableSections.length === 0 && matchingUnavailable.length === 0 && (
        <div className={styles.pickerEmpty}>No matching models</div>
      )}
    </div>
  );
}

function buildGroupSection<Provider extends string>(
  group: ModelGroup<Provider>,
  activeProvider: Provider,
  selection: ModelSelection,
  query: string,
) {
  const allModels: readonly CatalogEntry[] = group.catalog?.models ?? [];
  const models = query === ''
    ? allModels
    : allModels
      .map((model) => ({ model, score: modelSearchScore(group, model, query) }))
      .filter((entry) => entry.score > 0)
      .sort((left, right) => right.score - left.score)
      .map((entry) => entry.model);
  const defaultName = defaultNameOf(group.catalog);
  const fullProvider = providerLabel(group, group.provider);
  const shortProvider = shortProviderLabel(group, group.provider, fullProvider);
  const showDefault = (group.defaultAvailable !== false || (group.provider === activeProvider && selection.model === null))
    && (query === '' || scoreModelSearch([FOLLOW_DEFAULT_LABEL, defaultName, shortProvider, fullProvider, String(group.provider)], query) > 0);

  const authNotice = group.selectionProblem !== undefined && group.availability?.authentication_notice != null
    ? group.availability.authentication_notice.text
    : null;
  const { reason, refused } = catalogReason(group);
  const warningNotice = reason !== null && !refused
    ? `${fullProvider} is unavailable: ${reason} ${unavailableCreationNote(fullProvider)}`
    : null;

  const unreachable = group.catalog !== null && group.catalog.source === 'unavailable';
  const emptyCatalogMessage = group.catalog !== null && allModels.length === 0 && reason === null && query === ''
    ? (unreachable
      ? (group.selectionProblem !== undefined
        ? `${group.provider} model catalog is unavailable`
        : providerPresentation(group.provider)?.unavailable ?? `${group.provider} is unavailable`)
      : 'No models available on this account')
    : null;

  return { group, models, showDefault, authNotice, warningNotice, emptyCatalogMessage };
}

/** Deduplicated unavailability callouts for blocked providers. */
function UnavailableNoticeList<Provider extends string>({
  groups,
}: Readonly<{
  groups: readonly ModelGroup<Provider>[];
}>) {
  const authNotices: { key: string; text: string }[] = [];
  const groupedReasons: { key: string; labels: string[]; reason: string; refused: boolean }[] = [];

  for (const group of groups) {
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
  </>;
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

function ModelRow({
  label, isSelected, isDisabled = false, onSelect,
}: Readonly<{
  label: ReactNode;
  isSelected: boolean;
  isDisabled?: boolean;
  onSelect: (viaKeyboard: boolean) => void;
}>) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={isSelected}
      disabled={isDisabled}
      className={styles.pickerRow}
      onClick={(event) => onSelect(event.detail === 0)}
    >
      <span className={styles.pickerRowLabel}>{label}</span>
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
