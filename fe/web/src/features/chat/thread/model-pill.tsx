// The model picker in a planner conversation's composer footer, and on the new-track page. Presentational:
// every value is a prop; the queries and the write live in `app/router`. Each provider's catalog is one
// group; on the new-track page the pick also decides the Planner's provider (#1810), and each group
// follows its provider's availability (#1817). A Claude group is the Claude CLI's own list (#1822).
// Beside it, a Planner conversation's approval setting (#2348), and the Planner's role on the new-track page and in
// an existing Planner conversation, triggers of the same kind.

import * as RadixSlider from '@radix-ui/react-slider';
import { Button } from '@astryxdesign/core/Button';
import { DropdownMenu, DropdownMenuItem } from '@astryxdesign/core/DropdownMenu';
import { Divider } from '@astryxdesign/core/Divider';
import { HStack } from '@astryxdesign/core/HStack';
import { Icon as AstryxIcon } from '@astryxdesign/core/Icon';
import { Popover } from '@astryxdesign/core/Popover';
import { Text } from '@astryxdesign/core/Text';
import { VisuallyHidden } from '@astryxdesign/core/VisuallyHidden';
import { useCallback, useEffect, useRef, type KeyboardEvent, type ReactNode, type RefObject } from 'react';

import type { AgentProvider } from '../../../../../core/api/generated/wire.ts';
import type { PermissionModeOptionDto } from '../../../../../core/api/perga-v1.ts';
import {
  unavailableCreationNote, type ProviderAvailability,
} from '../../../../../core/domain/agent-providers.ts';
import type { ModelCatalog, ModelSelection } from '../../../../../core/domain/conversation.ts';
import { useState } from '../../../ui/state/public.ts';
import styles from './model-pill.module.css';

const FOLLOW_DEFAULT_LABEL = 'Default';

/**
 * Legacy Planner presentation only; managed groups use the declared provider identifier.
 * Total over AgentProvider, so a new backend is a compile error until its Planner presentation is declared.
 * `unavailable` says why that provider's `source: 'unavailable'` catalog is empty. A Claude group waits for
 * its availability and catalog before it joins a menu that offers other providers (`hiddenUntilKnown`); a
 * Codex one stays while the daemon is down, saying so. Only codex compacts (`switchNote`).
 * The host supplies the server's blank-creation capability only on the new-track surface.
 */
const PROVIDERS: Readonly<Record<AgentProvider, Readonly<{
  label: string; unavailable: string; hiddenUntilKnown: boolean; switchNote: boolean;
}>>> = Object.freeze({
  codex: Object.freeze({
    label: 'Codex', unavailable: 'codex is not running', hiddenUntilKnown: false, switchNote: true,
  }),
  claude: Object.freeze({
    label: 'Claude', unavailable: 'Claude cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
  opencode: Object.freeze({
    label: 'OpenCode', unavailable: 'OpenCode cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
  antigravity: Object.freeze({
    label: 'Antigravity (agy)', unavailable: 'Antigravity (agy) cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
  grok: Object.freeze({
    label: 'Grok', unavailable: 'Grok cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
});

/**
 * One provider's section of the menu: its catalog, `null` while it loads, and whether the provider can run
 * right now (`GET /api/agent-providers`). `availability` is `null` while that answer is unknown, and for a
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
}>;

function legacyPresentation(provider: string) {
  return Object.entries(PROVIDERS).find(([key]) => key === provider)?.[1];
}

/** Managed display needs no provider-specific UI registration. */
function providerLabel<Provider extends string>(group: ModelGroup<Provider> | undefined, provider: Provider): string {
  return group?.selectionProblem !== undefined ? provider : (legacyPresentation(provider)?.label ?? provider);
}

type CatalogEntry = ModelCatalog['models'][number];

/**
 * The groups a menu shows. The selection's own group always shows, so a pick never passes for another
 * provider's. Any other group is hidden while its provider is `not_configured` on this server.
 */
function visibleModelGroups<Provider extends string>(groups: readonly ModelGroup<Provider>[], provider: Provider): readonly ModelGroup<Provider>[] {
  return groups.filter((group) => group.selectionProblem !== undefined || group.provider === provider || (
    group.availability?.status !== 'not_configured'
    && (!legacyPresentation(group.provider)?.hiddenUntilKnown || (group.availability !== null && group.catalog !== null))));
}

/** Keep Escape inside a host Dialog, then restore focus only while it still belongs to this pill. */
function useEscapeFocus(open: boolean, setOpen: (open: boolean) => void, host: RefObject<HTMLSpanElement | null>) {
  const pendingFrame = useRef<number | null>(null);
  const cancelRestore = useCallback(() => {
    if (pendingFrame.current === null) return;
    cancelAnimationFrame(pendingFrame.current);
    pendingFrame.current = null;
  }, []);

  useEffect(() => cancelRestore, [cancelRestore]);
  useEffect(() => {
    if (open) cancelRestore();
  }, [open, cancelRestore]);

  const onOpenChange = (next: boolean) => {
    // Controlled Astryx layers acknowledge a close again from onHide. Keep that
    // acknowledgement from cancelling the restore for the same Escape.
    if (next !== open) cancelRestore();
    setOpen(next);
  };
  const onEscape = (event: KeyboardEvent<HTMLSpanElement>) => {
    if (event.key !== 'Escape') return;
    cancelRestore();
    if (!open) return;
    event.preventDefault();
    event.stopPropagation();
    const popup = event.target instanceof Element
      ? event.target.closest('[role="dialog"], [role="menu"]')
      : null;
    // Escape can start on the trigger inside a host Dialog. That parent
    // dialog is outside the pill and must not expand our focus boundary.
    const closingPopup = popup?.contains(event.currentTarget) ? null : popup;
    setOpen(false);
    pendingFrame.current = requestAnimationFrame(() => {
      pendingFrame.current = null;
      const active = document.activeElement;
      if (active === null || active === document.body || active === document.documentElement
        || host.current?.contains(active) || closingPopup?.contains(active)) {
        host.current?.querySelector('button')?.focus();
      }
    });
  };
  return { onEscape, onOpenChange };
}

/** The name of the default a catalog says is followed (for Claude, the model its CLI default resolves to), or `null` when it cannot say. */
function defaultNameOf(catalog: ModelCatalog | null): string | null {
  return catalog?.default_source === 'config_read' || catalog?.default_source === 'config_toml'
    || catalog?.default_source === 'claude_cli' || catalog?.default_source === 'acp_session'
    ? catalog.default.model
    : null;
}

function catalogReason<Provider extends string>(group: ModelGroup<Provider>): {
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

function effortsFor<Provider extends string>(
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
    ? catalog?.default.reasoning_effort ?? chosen?.default_reasoning_effort ?? null
    : chosen?.default_reasoning_effort ?? null;
  return { efforts, effortDefault };
}

export function ModelPill<Provider extends string = AgentProvider>({
  groups, provider, selection, onChange, isDisabled = false, placement = 'above', triggerId, effortControl = 'separate',
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
}>) {
  const [open, setOpen] = useState(false);
  const [viewedProvider, setViewedProvider] = useState<Provider>(provider);
  const [draft, setDraft] = useState<Readonly<{ provider: Provider; selection: ModelSelection }>>({
    provider,
    selection,
  });
  const hostRef = useRef<HTMLSpanElement | null>(null);
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

  const viewedGroup = shown.find((group) => group.provider === viewedProvider)
    ?? shown.find((group) => group.provider === provider)
    ?? shown[0];

  const handleOpenChange = (nextOpen: boolean) => {
    if (nextOpen) {
      setViewedProvider(provider);
      setDraft({ provider, selection });
    }
    onOpenChange(nextOpen);
  };

  const viewedIsDraft = viewedGroup !== undefined && viewedGroup.provider === draft.provider;
  const viewedModelId = viewedIsDraft ? draft.selection.model : null;
  const viewedEffortValue = viewedIsDraft ? draft.selection.reasoning_effort : null;
  const viewedEffortInfo = effortsFor(viewedGroup, viewedModelId);
  const viewedReason = viewedGroup ? catalogReason(viewedGroup) : { reason: null, refused: false, blocked: null };
  const draftGroup = shown.find((group) => group.provider === draft.provider);
  const draftBlocked = draftGroup === undefined || catalogReason(draftGroup).blocked !== null;

  const effortStops = viewedEffortInfo.efforts.map((entry) => ({
    value: entry.reasoning_effort,
    label: entry.reasoning_effort,
  }));
  const defaultStopIndex = viewedEffortInfo.effortDefault === null
    ? 0
    : Math.max(0, effortStops.findIndex((stop) => stop.value === viewedEffortInfo.effortDefault));
  const sliderIndex = viewedEffortValue === null
    ? defaultStopIndex
    : Math.max(0, effortStops.findIndex((stop) => stop.value === viewedEffortValue));
  const currentStopLabel = effortStops.at(sliderIndex)?.label ?? FOLLOW_DEFAULT_LABEL;
  const displayedEffortLabel = viewedEffortInfo.efforts.length === 0
    ? 'N/A'
    : viewedEffortValue === null && viewedEffortInfo.effortDefault !== null
      ? `${currentStopLabel} (${FOLLOW_DEFAULT_LABEL})`
      : currentStopLabel;

  const popoverContent = viewedGroup === undefined ? null : (
    <div className={styles.pickerPanel}>
      <div className={styles.pickerColumns}>
        <div className={styles.providerColumn} role="group" aria-label="Providers">
          {shown.map((group) => {
            const heading = providerLabel(group, group.provider);
            const isViewed = group.provider === viewedGroup.provider;
            const isSelectedProvider = group.provider === draft.provider;
            return (
              <button
                key={group.provider}
                type="button"
                role="radio"
                aria-checked={isViewed}
                data-nc-active={isViewed ? 'true' : 'false'}
                className={styles.pickerRow}
                onClick={() => {
                  setViewedProvider(group.provider);
                }}
              >
                <span className={styles.pickerRowLabel}>{heading}</span>
                {isSelectedProvider && (
                  <AstryxIcon icon="check" size="sm" color="accent" className={styles.pickerCheck} />
                )}
              </button>
            );
          })}
        </div>
        <div className={styles.modelColumn} role="group" aria-label="Models">
          <GroupChoices
            group={viewedGroup}
            selection={viewedIsDraft ? draft.selection : null}
            onChange={(next) => {
              setDraft({ provider: viewedGroup.provider, selection: next });
            }}
          />
        </div>
      </div>
      <Divider />
      <div className={styles.effortSection}>
        <div className={styles.effortHeader}>
          <span>Effort</span>
          <span className={styles.effortValue}>{displayedEffortLabel}</span>
        </div>
        <EffortSlider
          stops={viewedEffortInfo.efforts.length > 0 ? effortStops : [{ value: null, label: 'N/A' }]}
          selectedIndex={viewedEffortInfo.efforts.length > 0 ? sliderIndex : 0}
          isDisabled={viewedEffortInfo.efforts.length === 0 || viewedReason.blocked !== null}
          onChangeIndex={(idx) => {
            const stop = effortStops.at(idx);
            if (!stop) return;
            setDraft({
              provider: viewedGroup.provider,
              selection: { model: viewedModelId, reasoning_effort: stop.value },
            });
          }}
        />
      </div>
      <Divider />
      <div className={styles.pickerActions}>
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
          width="19rem"
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
            <Text type="inherit" color="inherit" maxLines={1} hasTruncateTooltip>
              {label}
            </Text>
          </Button>
        </Popover>
      </span>
    </HStack>
  );
}

/** One group's rows: Default, then the catalog. `selection` is `null` when the pick lies in another group. */
function GroupChoices<Provider extends string>({ group, selection, onChange }: Readonly<{
  group: ModelGroup<Provider>;
  selection: ModelSelection | null;
  onChange: (next: ModelSelection) => void;
}>) {
  const models: readonly CatalogEntry[] = group.catalog?.models ?? [];
  const defaultName = defaultNameOf(group.catalog);
  const unreachable = group.catalog !== null && group.catalog.source === 'unavailable';
  const { reason, refused, blocked } = catalogReason(group);
  const defaultLabel = group.catalog?.default_source === 'acp_session'
    ? `Session settings (${defaultName ?? 'current'})`
    : defaultName === null ? FOLLOW_DEFAULT_LABEL
      : group.catalog?.default_source === 'claude_cli' ? <ResolvedLabel name={FOLLOW_DEFAULT_LABEL} resolved={defaultName} />
        : `${FOLLOW_DEFAULT_LABEL} (${defaultName})`;

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
    <ModelRow
      label={defaultLabel}
      isSelected={selection !== null && selection.model === null}
      isDisabled={blocked !== null}
      onSelect={() => onChange({ model: null, reasoning_effort: null })}
    />
    {models.map((model) => (
      <ModelRow
        key={model.id}
        label={model.resolved_model === null ? model.display_name
          : <ResolvedLabel name={model.display_name} resolved={model.resolved_model} />}
        isSelected={selection !== null && selection.model === model.model}
        isDisabled={blocked !== null}
        /* Switching model drops the effort: one chosen for the previous model may not exist on this one. */
        onSelect={() => onChange({ model: model.model, reasoning_effort: null })}
      />
    ))}
    {models.length === 0 && reason === null && (
      <div className={styles.pickerEmpty}>
        {unreachable ? (group.selectionProblem !== undefined ? `${group.provider} model catalog is unavailable`
          : legacyPresentation(group.provider)?.unavailable ?? `${group.provider} is unavailable`) : 'No models available on this account'}
      </div>
    )}
  </>;
}

function ModelRow({
  label, isSelected, isDisabled = false, onSelect,
}: Readonly<{
  label: ReactNode;
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
      {isSelected && (
        <>
          <AstryxIcon icon="check" size="sm" color="accent" className={styles.pickerCheck} />
          <VisuallyHidden>Selected</VisuallyHidden>
        </>
      )}
    </button>
  );
}

function EffortSlider({
  stops, selectedIndex, isDisabled, onChangeIndex,
}: Readonly<{
  stops: readonly Readonly<{ value: string | null; label: string }>[];
  selectedIndex: number;
  isDisabled: boolean;
  onChangeIndex: (index: number) => void;
}>) {
  const segments = Math.max(1, stops.length - 1);

  return (
    <RadixSlider.Root
      className={styles.radixSliderRoot}
      min={0}
      max={segments}
      step={1}
      value={[selectedIndex]}
      disabled={isDisabled || stops.length <= 1}
      aria-label="Reasoning effort"
      onValueChange={([idx]) => {
        if (idx === undefined) return;
        onChangeIndex(idx);
      }}
      onValueCommit={([idx]) => {
        if (idx === undefined) return;
        onChangeIndex(idx);
      }}
    >
      <RadixSlider.Track className={styles.radixSliderTrack}>
        <RadixSlider.Range className={styles.radixSliderRange} />
        {stops.length > 1 && stops.map((stop, idx) => {
          const tickPos = idx === 0
            ? '6px'
            : idx === segments
              ? 'calc(100% - 6px)'
              : `${(idx / segments) * 100}%`;
          return (
            <span
              key={stop.value ?? idx}
              className={styles.radixSliderTick}
              data-nc-filled={idx <= selectedIndex ? 'true' : 'false'}
              style={{ insetInlineStart: tickPos }}
            />
          );
        })}
      </RadixSlider.Track>
      <RadixSlider.Thumb
        className={styles.radixSliderThumb}
        aria-label="Reasoning effort"
        aria-disabled={isDisabled || stops.length <= 1}
        aria-valuetext={stops.at(selectedIndex)?.label ?? FOLLOW_DEFAULT_LABEL}
      />
    </RadixSlider.Root>
  );
}

type SettingTone = 'neutral' | 'warning' | 'danger';
type SettingOption = Readonly<{ label: string; description: string; tone?: SettingTone }>;

/** Fallback options when no provider-declared list is supplied. */
const DEFAULT_PERMISSION_MODES: Readonly<Record<string, SettingOption>> = Object.freeze({
  never: Object.freeze({ label: 'Never', description: 'Sandbox only; never asks.' }),
  ask: Object.freeze({ label: 'Ask me', description: 'Pauses the turn to ask you.', tone: 'warning' }),
  full: Object.freeze({ label: 'Full access', description: "No sandbox; never asks. Runs with your account's access.", tone: 'danger' }),
});

/** The kernel reads the mode when it starts a turn, so a change never reaches the one already running. */
const PERMISSION_NOTE = 'Applies from the next turn.';

/** A session's approval setting: renders provider-declared modes or the default set when omitted. */
export function PermissionModePill({ mode, options, onChange, isDisabled = false, placement = 'above' }: Readonly<{
  /** What the session has stored (or selected in draft). */
  mode: string;
  options?: readonly PermissionModeOptionDto[];
  onChange: (mode: string) => void;
  isDisabled?: boolean;
  placement?: 'above' | 'below';
}>) {
  if (options !== undefined && options.length === 0) return null;
  const optionMap: Readonly<Record<string, SettingOption>> = options === undefined
    ? DEFAULT_PERMISSION_MODES
    : Object.fromEntries(options.map((opt) => [
      opt.id,
      { label: opt.label, description: opt.description, ...(opt.tone === 'neutral' ? {} : { tone: opt.tone }) },
    ]));
  const effectiveMode = Object.prototype.hasOwnProperty.call(optionMap, mode)
    ? mode
    : options !== undefined
      ? (options.find((opt) => opt.isDefault) ?? options[0]).id
      : 'never';
  return <SettingPill name="Approvals" options={optionMap} value={effectiveMode} onChange={onChange}
    note={PERMISSION_NOTE} isDisabled={isDisabled} placement={placement} />;
}

/** What a Track's Planner may do in its Area: an ordinary Planner, or an Area admin (`admin` on `POST`/`PATCH /api/tracks`). */
export type PlannerRole = 'member' | 'admin';

/** Total over `PlannerRole`, in menu order; Member is the default. */
const PLANNER_ROLES: Readonly<Record<PlannerRole, SettingOption>> = Object.freeze({
  member: Object.freeze({ label: 'Member', description: 'An ordinary Planner.' }),
  admin: Object.freeze({ label: 'Admin', description: 'May add Tracks and close the ones it added.' }),
});

/** The Planner's role beside a model picker, on the new-track page and in an existing Track's Planner conversation: a trigger of the approval setting's kind. */
export function PlannerRolePill({ value, onChange, isDisabled = false, disabledReason, placement = 'above' }: Readonly<{
  /** The role chosen; `role` would read as an ARIA role. */
  value: PlannerRole;
  onChange: (role: PlannerRole) => void;
  isDisabled?: boolean;
  /** Why it cannot be changed, as the disabled trigger's tooltip; it still names the current role. */
  disabledReason?: string;
  placement?: 'above' | 'below';
}>) {
  /* It sits at the trailing edge beside Create, so its menu opens leftward: room for its descriptions on a phone. */
  return <SettingPill name="Role" options={PLANNER_ROLES} value={value} onChange={onChange}
    isDisabled={isDisabled} tooltip={isDisabled ? disabledReason : undefined} placement={placement} alignment="end" />;
}

/** A short fixed setting: the trigger names it and its value, and each row explains itself. */
function SettingPill<Value extends string>({
  name, options, value, onChange, note, isDisabled, tooltip, placement, alignment = 'start',
}: Readonly<{
  name: string;
  options: Readonly<Record<Value, SettingOption>>;
  value: Value;
  onChange: (value: Value) => void;
  note?: string;
  isDisabled: boolean;
  /** With it Astryx marks a disabled trigger `aria-disabled` rather than `disabled`, so the tooltip stays reachable. */
  tooltip?: string;
  placement: 'above' | 'below';
  alignment?: 'start' | 'end';
}>) {
  const [open, setOpen] = useState(false);
  const hostRef = useRef<HTMLSpanElement | null>(null);
  const { onEscape, onOpenChange } = useEscapeFocus(open, setOpen, hostRef);
  const current = options[value];
  const tone = current.tone ?? 'neutral';
  const label = `${name}: ${current.label}`;
  return (
    <span ref={hostRef} className={styles.host} onKeyDownCapture={onEscape}>
      <DropdownMenu
        placement={placement}
        alignment={alignment}
        /* An `aria-disabled` trigger still passes ArrowDown to the menu, so a disabled one is held shut here. */
        isMenuOpen={open && !isDisabled}
        onOpenChange={(next) => { onOpenChange(next && !isDisabled); }}
        hasChevron={false}
        button={{
          label,
          children: <Text type="inherit" color="inherit" maxLines={1}>
            {`${name}: `}
            <span className={styles.settingValue} data-nc-tone={tone}>{current.label}</span>
          </Text>,
          'data-nc-tone': tone,
          variant: 'ghost',
          size: 'sm',
          isDisabled,
          tooltip,
          className: styles.setting,
        }}
      >
        {(Object.keys(options) as Value[]).map((option) => (
          /* A node, not a string: Astryx cuts a string description to one line, and Full access's says what it risks. */
          <Choice key={option} label={options[option].label}
            variant={options[option].tone === 'danger' ? 'destructive' : 'default'}
            description={<Text type="inherit" color="inherit">{options[option].description}</Text>}
            isSelected={value === option} onSelect={() => { if (option !== value) onChange(option); }} />
        ))}
        {note !== undefined && (
          <>
            <Divider />
            <div className={styles.settingNote} role="note">
              <Text type="supporting">{note}</Text>
            </div>
          </>
        )}
      </DropdownMenu>
    </span>
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

function Choice({
  label, description, isSelected, isDisabled = false, onSelect, variant = 'default',
}: Readonly<{
  label: ReactNode;
  description?: ReactNode;
  variant?: 'default' | 'destructive';
  isSelected: boolean;
  isDisabled?: boolean;
  onSelect: () => void;
}>) {
  return (
    <DropdownMenuItem
      label={label}
      variant={variant}
      className={variant === 'destructive' ? styles.destructiveChoice : undefined}
      description={description}
      onClick={onSelect}
      isDisabled={isDisabled}
      endContent={isSelected ? (
        <>
          <AstryxIcon icon="check" size="sm" color={variant === 'destructive' ? 'error' : 'accent'} />
          <VisuallyHidden>Selected</VisuallyHidden>
        </>
      ) : undefined}
    />
  );
}
