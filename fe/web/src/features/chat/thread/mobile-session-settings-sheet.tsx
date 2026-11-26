// Floating in-sheet card for mobile session model, reasoning effort, and permission mode.
// Floats cleanly above the composer inside the Chat Sheet with zero third-layer fullscreen scrim.

import { useCallback, useEffect, useRef } from 'react';
import type { PermissionModeOptionDto } from '../../../../../core/api/perga-v1.ts';
import type { ModelSelection } from '../../../../../core/domain/conversation.ts';
import type { AgentProvider } from '../../../../../core/domain/presentation-types.ts';
import { LayerDismissal } from '../../../ui/dialog/public.tsx';
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

function shortPermissionLabel(option: PermissionModeOptionDto): string {
  const trimmed = option.label.trim();
  if (trimmed.length <= 8) return trimmed;
  const firstWord = trimmed.split(/\s+/)[0];
  return firstWord || trimmed;
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

/** Whisper-quiet trigger above the mobile Chat composer + floating in-sheet card (Scheme A). */
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
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const cardRef = useRef<HTMLDivElement | null>(null);

  const closeCard = useCallback(() => {
    setOpen(false);
    triggerRef.current?.focus({ preventScroll: true });
  }, []);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      const target = event.target;
      if (!(target instanceof Node)) return;
      if (cardRef.current?.contains(target) || triggerRef.current?.contains(target)) return;
      setOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown, true);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown, true);
    };
  }, [open]);

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
    providerName: string;
    model: string | null;
    label: string;
    selected: boolean;
    disabled: boolean;
  }>> = [];

  for (const group of shown) {
    const blocked = catalogReason(group).blocked !== null;
    if (blocked) continue;
    const pName = providerLabel(group, group.provider);
    const groupModels = group.catalog?.models ?? [];
    if (groupModels.length === 0) {
      const def = defaultNameOf(group.catalog);
      modelChoices.push({
        key: `${group.provider}:default`,
        provider: group.provider,
        providerName: pName,
        model: null,
        label: def ?? FOLLOW_DEFAULT_LABEL,
        selected: group.provider === provider && selection.model === null,
        disabled: group.defaultAvailable === false,
      });
      continue;
    }
    for (const entry of groupModels) {
      const isSelected = group.provider === provider && (
        selection.model === entry.model
        || (selection.model === null && (entry.model === defaultName || entry.resolved_model === defaultName))
      );
      modelChoices.push({
        key: `${group.provider}:${entry.id}`,
        provider: group.provider,
        providerName: pName,
        model: entry.model,
        label: entry.display_name || entry.model,
        selected: isSelected,
        disabled: false,
      });
    }
  }

  const effectivePermissionMode = permission !== undefined && permission.options.length > 0
    ? (permission.options.some((opt) => opt.id === permission.mode)
      ? permission.mode
      : (permission.options.find((opt) => opt.isDefault) ?? permission.options[0]).id)
    : null;
  const hasPermissionOptions = permission !== undefined && permission.options.length > 0;
  const hasControls = modelChoices.length > 0 || visibleEfforts.length > 0 || hasPermissionOptions || activeBlockedReason !== null;

  return (
    <>
      <LayerDismissal
        isActive={open}
        getContainer={() => cardRef.current}
        onDismiss={closeCard}
      />
      <button
        ref={triggerRef}
        type="button"
        className={`${styles.trigger} ${open ? styles.triggerOpen : ''}`}
        aria-label={`Session model and mode: ${triggerLabel}`}
        aria-expanded={open}
        aria-haspopup="dialog"
        disabled={!hasControls || (modelDisabled && (permission === undefined || permission.disabled === true))}
        onClick={() => setOpen((current) => !current)}
      >
        <span className={styles.triggerText}>{triggerLabel}</span>
        <span className={styles.triggerChevron} aria-hidden="true">
          <Icon name="chevron-down" size="sm" />
        </span>
      </button>

      {open && (
        <div
          ref={cardRef}
          role="dialog"
          aria-label="Session model and mode"
          className={styles.floatingCard}
        >
          <header className={styles.header}>
            <h2 className={styles.title}>Session model &amp; mode</h2>
            <button
              type="button"
              className={styles.closeButton}
              aria-label="Close session model and mode"
              onClick={closeCard}
            >
              <Icon name="close" size="sm" />
            </button>
          </header>

          <div className={styles.body}>
            {modelChoices.length > 0 ? (
              <section className={styles.section} aria-label="Provider and model">
                <h3 className={styles.sectionTitle}>Provider &amp; model</h3>
                <ul className={styles.groupCard} role="radiogroup" aria-label="Models">
                  {modelChoices.map((choice) => (
                    <li
                      key={choice.key}
                      className={`${styles.groupItem} ${choice.selected ? styles.groupItemSelected : ''}`}
                    >
                      <button
                        type="button"
                        role="radio"
                        aria-checked={choice.selected}
                        disabled={modelDisabled || choice.disabled}
                        className={styles.modelRow}
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
                            {choice.providerName}{choice.selected ? ' · Selected' : ''}
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

            {(visibleEfforts.length > 0 || hasPermissionOptions) && (
              <section className={styles.section} aria-label="Reasoning and permission">
                <h3 className={styles.sectionTitle}>Reasoning &amp; permission</h3>
                <div className={styles.groupCard}>
                  {visibleEfforts.length > 0 && (
                    <div className={styles.settingRow}>
                      <span className={styles.settingLabel}>
                        <span className={styles.settingIcon} aria-hidden="true">
                          <Icon name="sliders" size="sm" />
                        </span>
                        <span>Effort</span>
                      </span>
                      <div className={styles.segmented} role="radiogroup" aria-label="Reasoning effort">
                        {visibleEfforts.map((entry) => {
                          const active = effectiveEffort === entry.reasoning_effort;
                          return (
                            <button
                              key={entry.reasoning_effort}
                              type="button"
                              role="radio"
                              aria-checked={active}
                              disabled={modelDisabled}
                              className={`${styles.segment} ${active ? styles.segmentActive : ''}`}
                              onClick={() => onChangeModel(
                                { model: selection.model, reasoning_effort: entry.reasoning_effort },
                                provider,
                              )}
                            >
                              {shortEffortLabel(entry.reasoning_effort)}
                            </button>
                          );
                        })}
                      </div>
                    </div>
                  )}

                  {hasPermissionOptions && (
                    <div className={`${styles.settingRow} ${visibleEfforts.length > 0 ? styles.settingRowDivided : ''}`}>
                      <span className={styles.settingLabel}>
                        <span className={styles.settingIcon} aria-hidden="true">
                          <Icon name="shield" size="sm" />
                        </span>
                        <span>Permission</span>
                      </span>
                      <div className={styles.segmented} role="radiogroup" aria-label="Permission mode">
                        {permission.options.map((opt) => {
                          const active = effectivePermissionMode === opt.id;
                          return (
                            <button
                              key={opt.id}
                              type="button"
                              role="radio"
                              aria-checked={active}
                              aria-label={opt.label}
                              title={opt.label}
                              disabled={permission.disabled === true}
                              className={`${styles.segment} ${active ? styles.segmentActive : ''}`}
                              onClick={() => { if (opt.id !== effectivePermissionMode) permission.onChange(opt.id); }}
                            >
                              {shortPermissionLabel(opt)}
                            </button>
                          );
                        })}
                      </div>
                    </div>
                  )}
                </div>
              </section>
            )}

            {appliesNextTurn && (
              <p className={styles.note} role="note">{PERMISSION_NOTE}</p>
            )}
          </div>
        </div>
      )}
    </>
  );
}
