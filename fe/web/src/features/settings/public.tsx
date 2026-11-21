import type { SystemUpgradeState } from '../../../../core/domain/system-upgrade.ts';
import { upgradeReceiptSummary } from '../../../../core/domain/system-upgrade.ts';
// Settings — workspace preferences, and the one row grammar every pane uses.
// A row is either something you set (`control`) or somewhere you go (`onOpen`), never both.

import { Heading as AstryxHeading } from '@astryxdesign/core/Heading';
import { List as AstryxList,ListItem as AstryxListItem } from '@astryxdesign/core/List';
import { Selector as AstryxSelector } from '@astryxdesign/core/Selector';
import { SideNav as AstryxSideNav,SideNavItem as AstryxSideNavItem } from '@astryxdesign/core/SideNav';
import { Text as AstryxText } from '@astryxdesign/core/Text';
import { type ReactNode } from 'react';

import { Icon } from '../../ui/icon/public.tsx';
import {
SETTINGS_SECTIONS,SettingsIndex,
type SettingsPresentation,
type SettingsSection,type SettingsSectionEntry
} from './navigation.tsx';
import styles from './settings.module.css';

export type SettingsSurfaceProps = Readonly<{
  presentation: SettingsPresentation;
  section: SettingsSection;
  sections?: readonly SettingsSectionEntry[];
  onSelectSection: (section: SettingsSection) => void;
  children: ReactNode;
}>;

/** The two-column frame every Settings route renders inside. `aria-current="page"` is stamped on top of `isSelected`: the current route is a fact a screen reader has to be told. */
export function SettingsSurface({ presentation, section, sections = SETTINGS_SECTIONS, onSelectSection, children }: SettingsSurfaceProps) {
  return (
    <div className={`${styles.surface} ${presentation === 'desktop' ? '' : styles.mobileSurface} ${presentation === 'mobile-detail' ? styles.mobileDetail : ''}`}>
      {presentation === 'mobile-index' && <SettingsIndex sections={sections} onSelectSection={onSelectSection} />}
      {presentation === 'desktop' && <AstryxSideNav aria-label="Settings sections" className={styles.sectionNav}>
        {sections.map((entry) => (
          <AstryxSideNavItem key={entry.id} label={entry.label} icon={entry.icon}
            isSelected={entry.id === section} aria-current={entry.id === section ? 'page' : undefined}
            onClick={() => onSelectSection(entry.id)} />
        ))}
      </AstryxSideNav>}
      <div className={styles.pane} hidden={presentation === 'mobile-index'}>{children}</div>
    </div>
  );
}

/** A pane: its heading, one sentence saying what the group is for, and its rows. */
export function SettingsPane({ title, lede, children, category }: Readonly<{
  title: string;
  /** Present only on a top-level category; drill-ins keep their own heading. */
  category?: SettingsSection;
  lede?: string;
  children: ReactNode;
}>) {
  const headingId = `nc-settings-${title.toLowerCase().replace(/\s+/g, '-')}`;
  return (
    <div className={styles.paneBody}>
      <section className={styles.group} aria-labelledby={headingId}>
        <AstryxHeading level={3} id={headingId} className={category === undefined ? undefined : styles.categoryHeading}>{title}</AstryxHeading>
        {lede !== undefined && <AstryxText as="p" color="secondary">{lede}</AstryxText>}
        {children}
      </section>
    </div>
  );
}

/** The rows of a pane. Hairlines between them and nothing else. */
export function SettingsList({ children }: Readonly<{ children: ReactNode }>) {
  return <AstryxList hasDividers density="balanced" className={styles.list}>{children}</AstryxList>;
}

/** One row. `control` and `onOpen` are mutually exclusive by type. */
export type SettingRowProps = Readonly<{
  title: string;
  /** A quiet second mark on the title line — a version, a count, a unit. Not a place for a sentence. */
  titleSuffix?: ReactNode;
  /** One sentence. Omitted when the title already says everything. */
  description?: ReactNode;
  /** A badge or status mark before the title. */
  startContent?: ReactNode;
}> & (
  | Readonly<{ control: ReactNode; onOpen?: never; stackControlOnMobile?: boolean }>
  | Readonly<{ onOpen: () => void; control?: never; stackControlOnMobile?: never }>
);

export function SettingRow({
  title, titleSuffix, description, startContent, control, onOpen, stackControlOnMobile = true,
}: SettingRowProps) {
  return (
    <AstryxListItem
      className={`${styles.row} ${onOpen === undefined && stackControlOnMobile ? styles.controlRow : ''}`}
      label={titleSuffix === undefined ? title : (
        <span className={styles.rowTitle} data-nc-row-title="">
          {title}
          <span className={styles.rowTitleSuffix}>{titleSuffix}</span>
        </span>
      )}
      description={description}
      startContent={startContent}
      endContent={onOpen === undefined ? control : <Icon name="chevron-right" />}
      onClick={onOpen}
    />
  );
}

/** Mirrors `app/theme`'s mode union by value: `features/**` must not import `app/**`. */
export type ThemeMode = 'light' | 'dark' | 'system';

const THEME_OPTIONS = Object.freeze([
  Object.freeze({ value: 'light', label: 'Light' }),
  Object.freeze({ value: 'dark', label: 'Dark' }),
  Object.freeze({ value: 'system', label: 'System' }),
] as const);

/** Every right-hand control is this wide, so the pane has one trailing edge. Exported for the plugin configuration pane. */
export const CONTROL_WIDTH = 260;

export function AppearancePane({ themeMode, onThemeModeChange }: Readonly<{
  themeMode: ThemeMode;
  onThemeModeChange: (mode: ThemeMode) => void;
}>) {
  return (
    <SettingsPane category="appearance" title="Appearance" lede="How this device paints the app. Not shared with your other devices.">
      <SettingsList>
        <SettingRow
          title="Theme"
          description="System follows your operating system's setting."
          control={(
            <AstryxSelector
              label="Theme"
              isLabelHidden
              value={themeMode}
              options={[...THEME_OPTIONS]}
              onChange={(value) => onThemeModeChange(asThemeMode(value))}
              width={CONTROL_WIDTH}
            />
          )}
        />
      </SettingsList>
    </SettingsPane>
  );
}

function asThemeMode(value: string): ThemeMode {
  return value === 'light' || value === 'dark' ? value : 'system';
}

export function AboutPane({ system }: Readonly<{ system?: SystemUpgradeState | null }> = {}) {
  return (
    <SettingsPane category="about" title="About" lede="What this build is. Read-only.">
      <SettingsList>
        <SettingRow title="Version" control={<span className={styles.aboutValue}>{__NC_VERSION__}</span>} />
        <SettingRow title="Build" control={<span className={styles.aboutValue}>{__NC_BUILD__}</span>} />
        {system && <>
          <SettingRow title="releaseId" control={<span className={styles.aboutValue}>{system.info?.releaseId ?? 'Loading…'}</span>} />
          <SettingRow title="webBuildId" control={<span className={styles.aboutValue}>{system.info?.webBuildId ?? 'Loading…'}</span>} />
          <SettingRow title="hostBuildId" control={<span className={styles.aboutValue}>{system.info?.hostBuildId ?? 'Loading…'}</span>} />
          <SettingRow title="schemaVersion" control={<span className={styles.aboutValue}>{system.info ? String(system.info.schemaVersion) : 'Loading…'}</span>} />
          <SettingRow title="draining" control={<span className={styles.aboutValue}>{String(system.draining)}</span>} />
          <SettingRow title="activeDispatches" control={<span className={styles.aboutValue}>{String(system.activeDispatches)}</span>} />
          {system.receipt && <SettingRow title="Latest upgrade receipt" control={<span className={styles.aboutValue}>{upgradeReceiptSummary(system.receipt)}</span>} />}
        </>}
      </SettingsList>
    </SettingsPane>
  );
}
