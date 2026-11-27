import { SegmentedControl, SegmentedControlItem } from '@astryxdesign/core/SegmentedControl';

import { Icon, type IconName } from '../../ui/icon/public.tsx';
import { SettingRow, SettingsList } from './pane.tsx';
import styles from './settings.module.css';

type SegmentedOption = Readonly<{ value: string; label: string; icon?: IconName }>;

export type SegmentedSettingField = Readonly<{
  id: string;
  label: string;
  description?: string;
  control: 'segmented';
  options: readonly SegmentedOption[];
}>;

type FieldBinding<Value extends string> = Readonly<{
  value: Value;
  onChange: (value: Value) => void;
}>;

type BoundSettingField = SegmentedSettingField & FieldBinding<string>;

export function bindSettingField<const Field extends SegmentedSettingField>(
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

export function SettingFieldsList({ fields }: Readonly<{ fields: readonly BoundSettingField[] }>) {
  return (
    <SettingsList>
      {fields.map((field) => (
        <SettingRow key={field.id}
          title={field.label}
          description={field.description === undefined ? undefined : <span>{field.description}</span>}
          control={(
            <SegmentedControl className={styles.themeControl} size="sm" label={field.label}
              value={field.value} onChange={field.onChange}>
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
