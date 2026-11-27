import { SegmentedControl, SegmentedControlItem } from '@astryxdesign/core/SegmentedControl';
import { Selector } from '@astryxdesign/core/Selector';

import type { SettingsSnapshotDto } from '../../../../core/api/generated/wire.ts';
import { Icon, type IconName } from '../../ui/icon/public.tsx';
import { SettingRow, SettingsList } from './pane.tsx';
import styles from './settings.module.css';

type SettingOption = Readonly<{ value: string; label: string; icon?: IconName }>;

type SettingField = Readonly<{
  id: string;
  label: string;
  description?: string;
  control: 'segmented' | 'select';
  options: readonly SettingOption[];
}>;

export type SegmentedSettingField = SettingField & Readonly<{ control: 'segmented' }>;

type FieldBinding<Value extends string> = Readonly<{
  value: Value;
  onChange: (value: Value) => void;
}>;

type BoundSettingField = SettingField & FieldBinding<string>;

export function bindSettingField<const Field extends SettingField>(
  field: Field,
  binding: FieldBinding<NoInfer<Field['options'][number]['value']>>,
): BoundSettingField {
  return {
    ...field,
    value: binding.value,
    // Astryx accepts strings and emits values from the items rendered below.
    onChange: (value) => binding.onChange(value),
  };
}

export function bindSettingsSection(
  snapshot: SettingsSnapshotDto,
  section: string,
  onChange: (key: string, value: string | number) => void,
): BoundSettingField[] {
  return snapshot.fields.flatMap((field) => {
    if (field.section !== section || (field.control !== 'segmented' && field.control !== 'select')) return [];
    const current = snapshot.values[field.key];
    const integer = field.valueType === 'integer' && typeof current === 'number' && Number.isSafeInteger(current);
    const text = field.valueType === 'string' && typeof current === 'string';
    if (!integer && !text) return [];

    const value = String(current);
    const presets = field.options ?? [];
    const suffix = field.unit === null ? '' : field.unit === '%' ? '%' : ` ${field.unit}`;
    const options = presets.some((option) => option.value === value)
      ? presets : [...presets, { value, label: `${value}${suffix}` }];
    return [{
      id: field.key, label: field.label, description: field.description ?? undefined,
      control: field.control, options, value,
      onChange: (selected) => {
        if (selected === value) return;
        const next = integer ? Number(selected) : selected;
        if (typeof next === 'string' || Number.isSafeInteger(next)) onChange(field.key, next);
      },
    }];
  });
}

export function SettingFieldsList({ fields, disabled = false, density }: Readonly<{
  fields: readonly BoundSettingField[];
  disabled?: boolean;
  density?: 'compact' | 'balanced';
}>) {
  return (
    <SettingsList density={density}>
      {fields.map((field) => (
        <SettingRow key={field.id}
          title={field.label}
          description={field.description === undefined ? undefined : <span>{field.description}</span>}
          control={field.control === 'select' ? (
            <div className={styles.selectorSlot}>
              <Selector width="100%" size="sm" label={field.label} isLabelHidden
                options={[...field.options]} value={field.value} onChange={field.onChange}
                isDisabled={disabled} disabledMessage="Saving planner defaults…" />
            </div>
          ) : (
            <SegmentedControl className={styles.segmentedControl} size="sm" label={field.label}
              value={field.value} onChange={field.onChange} isDisabled={disabled}>
              {field.options.map((option) => (
                <SegmentedControlItem key={option.value} value={option.value} label={option.label}
                  icon={option.icon === undefined ? undefined : <Icon name={option.icon} size="sm" />} />
              ))}
            </SegmentedControl>
          )}
        />
      ))}
    </SettingsList>
  );
}
