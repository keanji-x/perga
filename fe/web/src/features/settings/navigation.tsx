// One category roster drives desktop navigation, the mobile index and page titles.
import { List, ListItem } from '@astryxdesign/core/List';
import { Icon, type IconName } from '../../ui/icon/public.tsx';
import { MobileListGroup } from '../../ui/mobile-list/public.tsx';
import styles from './settings.module.css';

type SectionMetadata = Readonly<{ id: string; label: string; icon: IconName; lede?: string }>;

export const SETTINGS_SECTIONS = Object.freeze([
  Object.freeze({
    id: 'appearance', label: 'Appearance', icon: 'palette',
    lede: 'How this device displays Perga. Stored locally in this browser.',
  }),
  Object.freeze({
    id: 'planners', label: 'Planners', icon: 'agent',
    lede: 'Server providers available to run tracks.',
  }),
  Object.freeze({ id: 'account', label: 'Account', icon: 'shield' }),
  Object.freeze({ id: 'about', label: 'About', icon: 'info' }),
] as const satisfies readonly SectionMetadata[]);

export type SettingsSectionEntry = (typeof SETTINGS_SECTIONS)[number];
export type SettingsSection = SettingsSectionEntry['id'];
export type SettingsPresentation = 'desktop' | 'mobile-index' | 'mobile-detail';

export function settingsSection(section: SettingsSection): SectionMetadata {
  // The ID type is derived from this roster, so every valid ID has an entry.
  return SETTINGS_SECTIONS.find((entry) => entry.id === section)!;
}

export function SettingsIndex({ onSelectSection, sections = SETTINGS_SECTIONS }: Readonly<{
  onSelectSection: (section: SettingsSection) => void;
  sections?: readonly SettingsSectionEntry[];
}>) {
  return <nav aria-label="Settings categories">
    <MobileListGroup label="Settings">
      <List hasDividers={false} density="balanced" className={styles.categoryList}>
        {sections.map((entry) => <ListItem key={entry.id} label={entry.label}
          className={styles.categoryRow}
          startContent={<Icon name={entry.icon} size="sm" />}
          endContent={<Icon name="chevron-right" size="sm" />}
          onClick={() => onSelectSection(entry.id)} />)}
      </List>
    </MobileListGroup>
  </nav>;
}
