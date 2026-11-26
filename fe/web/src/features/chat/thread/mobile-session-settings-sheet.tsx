// Anchored floating card for mobile session model, reasoning effort, and permission mode.
// Built on Astryx Popover (presentation="popover", placement="above") so layer stacking,
// Escape dismissal, outside light-dismiss, and focus restoration share one runtime contract.

import { Popover } from '@astryxdesign/core/Popover';
import type { KeyboardEvent } from 'react';
import type { PermissionModeOptionDto } from '../../../../../core/api/perga-v1.ts';
import type { ModelSelection } from '../../../../../core/domain/conversation.ts';
import type { AgentProvider } from '../../../../../core/domain/presentation-types.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import {
  catalogReason,
  defaultNameOf,
  effortsFor,
  FOLLOW_DEFAULT_LABEL,
  providerLabel,
  visibleModelGroups,
  type ModelGroup,
} from './model-selection.ts';
import { PERMISSION_NOTE } from './setting-pill.tsx';
import styles from './mobile-session-settings-sheet.module.css';

function shortEffortLabel(value: string): string {
  const lower = value.toLowerCase();
  if (lower === 'medium') return 'Med';
  if (lower === 'minimal') return 'Min';
  return value.charAt(0).toUpperCase() + value.slice(1);
}

/** Standard WAI-ARIA radiogroup keyboard navigation (Arrow keys + Home/End move focus and select). */
function handleRadioKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
  const { key } = event;
  if (key !== 'ArrowDown' && key !== 'ArrowRight' && key !== 'ArrowUp' && key !== 'ArrowLeft' && key !== 'Home' && key !== 'End') {
    return;
  }
  const group = event.currentTarget.closest<HTMLElement>('[role="radiogroup"]');
  if (group === null) return;
  const radios = Array.from(group.querySelectorAll<HTMLButtonElement>('[role="radio"]:not(:disabled)'));
  if (radios.length === 0) return;
  const currentIndex = radios.indexOf(event.currentTarget);
  let nextIndex = currentIndex === -1 ? 0 : currentIndex;
  if (key === 'Home') nextIndex = 0;
  else if (key === 'End') nextIndex = radios.length - 1;
  else if (key === 'ArrowDown' || key === 'ArrowRight') nextIndex = (nextIndex + 1) % radios.length;
  else nextIndex = (nextIndex - 1 + radios.length) % radios.length;
  event.preventDefault();
  const target = radios[nextIndex];
  target.focus();
  target.click();
}

export type MobileSessionSettingsControlProps<Provider extends string = AgentProvider> = Readonly<{
  groups: readonly ModelGroup<Provider>[];
  provider: Provider;
  selection: ModelSelection;
  onChangeModel: (next: ModelSelection, provider: Provider) => void;
  modelDisabled?: boolean;
  appliesNextTurn?: boolean;
  permission?: Readonly<{
    mode: string;
    options: readonly PermissionModeOptionDto[];
    onChange: (mode: string) => void;
    disabled?: boolean;
  }>;
}>;

/** Whisper-quiet trigger above the mobile Chat composer + anchored Popover card (Scheme A). */
export function MobileSessionSettingsControl<Provider extends string = AgentProvider>({
  groups,
  provider,
  selection,
  onChangeModel,
  modelDisabled = false,
  appliesNextTurn = false,
  permission,
}: MobileSessionSettingsControlProps<Provider>) {
  const [open, setOpen] = useState(false);

  const shown = visibleModelGroups(groups, provider);
  const activeGroup = shown.find((group) => group.provider === provider) ?? shown[0];
  const catalog = activeGroup?.catalog ?? null;
  const models = catalog?.models ?? [];
  const defaultName = defaultNameOf(catalog);
  const chosen = selection.model === null
    ? undefined
    : models.find((entry) => entry.model === selection.model);
  const defaultEntry = defaultName === null
    ? undefined
    : models.find((entry) => entry.model === defaultName || entry.resolved_model === defaultName);
  const modelName = selection.model === null
    ? (defaultEntry?.display_name ?? defaultName ?? FOLLOW_DEFAULT_LABEL)
    : (chosen?.display_name ?? selection.model);

  const activeBlockedReason = activeGroup === undefined ? null : catalogReason(activeGroup).blocked;
  const { efforts, effortDefault } = effortsFor(activeGroup, selection.model);
  const visibleEfforts = activeBlockedReason !== null ? [] : efforts;
  const effectiveEffort = selection.reasoning_effort ?? effortDefault;
  const triggerLabel = effectiveEffort !== null
    ? `${modelName} · ${shortEffortLabel(effectiveEffort)}`
    : modelName;

  const modelChoices: Array<Readonly<{
    key: string;
    provider: Provider;
    model: string | null;
    label: string;
    meta: string;
    selected: boolean;
    disabled: boolean;
  }>> = [];

  for (const group of shown) {
    if (catalogReason(group).blocked !== null) continue;
    const pName = providerLabel(group, group.provider);
    const groupModels = group.catalog?.models ?? [];
    const groupDefaultName = defaultNameOf(group.catalog);
    const groupDefaultEntry = groupDefaultName === null
      ? undefined
      : groupModels.find((entry) => entry.model === groupDefaultName || entry.resolved_model === groupDefaultName);
    const resolvedDefaultLabel = groupDefaultEntry?.display_name ?? groupDefaultName;

    if (group.defaultAvailable !== false || groupModels.length === 0) {
      const selected = group.provider === provider && selection.model === null;
      modelChoices.push({
        key: `${group.provider}:default`,
        provider: group.provider,
        model: null,
        label: FOLLOW_DEFAULT_LABEL,
        meta: resolvedDefaultLabel !== null ? `${pName} · ${resolvedDefaultLabel}` : pName,
        selected,
        disabled: group.defaultAvailable === false,
      });
    }

    for (const entry of groupModels) {
      modelChoices.push({
        key: `${group.provider}:${entry.id}`,
        provider: group.provider,
        model: entry.model,
        label: entry.display_name || entry.model,
        meta: pName,
        selected: group.provider === provider && selection.model === entry.model,
        disabled: false,
      });
    }
  }

  const unavailableEffort = activeBlockedReason === null
    && selection.reasoning_effort !== null
    && !visibleEfforts.some((entry) => entry.reasoning_effort === selection.reasoning_effort)
    ? selection.reasoning_effort
    : null;

  const effortChoices: Array<Readonly<{
    key: string;
    value: string | null;
    label: string;
    selected: boolean;
    disabled: boolean;
  }>> = visibleEfforts.length === 0 && unavailableEffort === null ? [] : [
    {
      key: 'default',
      value: null,
      label: 'Default',
      selected: selection.reasoning_effort === null,
      disabled: false,
    },
    ...visibleEfforts.map((entry) => ({
      key: entry.reasoning_effort,
      value: entry.reasoning_effort,
      label: shortEffortLabel(entry.reasoning_effort),
      selected: selection.reasoning_effort === entry.reasoning_effort,
      disabled: false,
    })),
    ...(unavailableEffort === null ? [] : [{
      key: `unavailable:${unavailableEffort}`,
      value: unavailableEffort,
      label: shortEffortLabel(unavailableEffort),
      selected: true,
      disabled: true,
    }]),
  ];

  const effectivePermissionMode = permission !== undefined && permission.options.length > 0
    ? (permission.options.some((opt) => opt.id === permission.mode)
      ? permission.mode
      : (permission.options.find((opt) => opt.isDefault) ?? permission.options[0]).id)
    : null;
  const hasPermissionOptions = permission !== undefined && permission.options.length > 0;
  const hasControls = modelChoices.length > 0 || effortChoices.length > 0 || hasPermissionOptions || activeBlockedReason !== null;
  const triggerDisabled = !hasControls || (modelDisabled && (permission === undefined || permission.disabled === true));

  const firstEnabledModelIndex = modelChoices.findIndex((choice) => !(modelDisabled || choice.disabled));
  const selectedModelIndex = modelChoices.findIndex((choice) => choice.selected);
  const activeModelTabIndex = selectedModelIndex >= 0 ? selectedModelIndex : firstEnabledModelIndex;

  const selectedEffortIndex = effortChoices.findIndex((choice) => choice.selected && !choice.disabled);
  const activeEffortTabIndex = selectedEffortIndex >= 0 ? selectedEffortIndex : 0;

  const selectedPermissionIndex = hasPermissionOptions
    ? permission.options.findIndex((opt) => opt.id === effectivePermissionMode)
    : -1;
  const activePermissionTabIndex = selectedPermissionIndex >= 0 ? selectedPermissionIndex : 0;

  const cardContent = (
    <div className={styles.floatingCard}>
      <header className={styles.header}>
        <h2 className={styles.title}>Session model &amp; mode</h2>
        <button
          type="button"
          className={styles.closeButton}
          aria-label="Close session model and mode"
          onClick={() => setOpen(false)}
        >
          <Icon name="close" size="sm" />
        </button>
      </header>

      <div className={styles.body}>
        {modelChoices.length > 0 ? (
          <section className={styles.section} aria-label="Provider and model">
            <h3 className={styles.sectionTitle}>Provider &amp; model</h3>
            <ul
              className={styles.groupCard}
              role="radiogroup"
              aria-label="Models"
            >
              {modelChoices.map((choice, index) => (
                <li
                  key={choice.key}
                  className={`${styles.groupItem} ${choice.selected ? styles.groupItemSelected : ''}`}
                >
                  <button
                    type="button"
                    role="radio"
                    aria-checked={choice.selected}
                    tabIndex={index === activeModelTabIndex ? 0 : -1}
                    disabled={modelDisabled || choice.disabled}
                    className={styles.modelRow}
                    onKeyDown={handleRadioKeyDown}
                    onClick={() => {
                      if (choice.selected) return;
                      onChangeModel({ model: choice.model, reasoning_effort: null }, choice.provider);
                    }}
                  >
                    <span className={styles.modelIcon} aria-hidden="true">
                      <Icon name="agent" size="sm" />
                    </span>
                    <span className={styles.modelCopy}>
                      <span className={styles.modelName}>{choice.label}</span>
                      <span className={styles.modelMeta}>
                        {choice.meta}{choice.selected ? ' · Selected' : ''}
                      </span>
                    </span>
                    <span className={styles.modelTrailing} aria-hidden="true">
                      <Icon name={choice.selected ? 'check' : 'chevron-right'} size="sm" />
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          </section>
        ) : activeBlockedReason !== null && (
          <p className={styles.note} role="note">{activeBlockedReason}</p>
        )}

        {effortChoices.length > 0 && (
          <section className={styles.section} aria-label="Reasoning effort">
            <h3 className={styles.sectionTitle}>Reasoning</h3>
            <div className={styles.groupCard}>
              <div className={styles.settingRow}>
                <span className={styles.settingLabel}>
                  <span className={styles.settingIcon} aria-hidden="true">
                    <Icon name="sliders" size="sm" />
                  </span>
                  <span>Effort</span>
                </span>
                <div
                  className={styles.segmented}
                  role="radiogroup"
                  aria-label="Reasoning effort"
                >
                  {effortChoices.map((choice, index) => (
                    <button
                      key={choice.key}
                      type="button"
                      role="radio"
                      aria-checked={choice.selected}
                      tabIndex={index === activeEffortTabIndex ? 0 : -1}
                      disabled={modelDisabled || choice.disabled}
                      className={`${styles.segment} ${choice.selected ? styles.segmentActive : ''}`}
                      onKeyDown={handleRadioKeyDown}
                      onClick={() => {
                        if (choice.selected) return;
                        onChangeModel({ model: selection.model, reasoning_effort: choice.value }, provider);
                      }}
                    >
                      {choice.label}
                    </button>
                  ))}
                </div>
              </div>
            </div>
          </section>
        )}

        {hasPermissionOptions && (
          <section className={styles.section} aria-label="Permission mode">
            <h3 className={styles.sectionTitle}>Permission</h3>
            <ul
              className={styles.groupCard}
              role="radiogroup"
              aria-label="Permission mode"
            >
              {permission.options.map((opt, index) => {
                const active = effectivePermissionMode === opt.id;
                const danger = opt.tone === 'danger';
                return (
                  <li
                    key={opt.id}
                    className={`${styles.groupItem} ${active ? styles.groupItemSelected : ''} ${danger ? styles.permissionDanger : ''}`}
                  >
                    <button
                      type="button"
                      role="radio"
                      aria-checked={active}
                      tabIndex={index === activePermissionTabIndex ? 0 : -1}
                      disabled={permission.disabled === true}
                      className={styles.permissionRow}
                      onKeyDown={handleRadioKeyDown}
                      onClick={() => { if (opt.id !== effectivePermissionMode) permission.onChange(opt.id); }}
                    >
                      <span className={styles.modelIcon} aria-hidden="true">
                        <Icon name="shield" size="sm" />
                      </span>
                      <span className={styles.modelCopy}>
                        <span className={styles.permissionName}>{opt.label}</span>
                        {opt.description !== '' && (
                          <span className={styles.permissionDescription}>{opt.description}</span>
                        )}
                      </span>
                      <span className={styles.modelTrailing} aria-hidden="true">
                        <Icon name={active ? 'check' : 'chevron-right'} size="sm" />
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </section>
        )}

        {appliesNextTurn && (
          <p className={styles.note} role="note">{PERMISSION_NOTE}</p>
        )}
      </div>
    </div>
  );

  return (
    <Popover
      presentation="popover"
      placement="above"
      alignment="start"
      width="min(calc(100vw - var(--space-6)), 23rem)"
      padding={0}
      label="Session model and mode"
      hasCloseButton={false}
      className={styles.popoverSurface}
      isOpen={open && !triggerDisabled}
      onOpenChange={(next) => setOpen(next && !triggerDisabled)}
      isEnabled={!triggerDisabled}
      content={cardContent}
    >
      {({ ref, onClick, ...ariaProps }) => (
        <button
          ref={ref}
          type="button"
          className={`${styles.trigger} ${open ? styles.triggerOpen : ''}`}
          aria-label={`Session model and mode: ${triggerLabel}`}
          disabled={triggerDisabled}
          {...ariaProps}
          onClick={(event) => onClick(event)}
        >
          <span className={styles.triggerText}>{triggerLabel}</span>
          <span className={styles.triggerChevron} aria-hidden="true">
            <Icon name="chevron-down" size="sm" />
          </span>
        </button>
      )}
    </Popover>
  );
}
