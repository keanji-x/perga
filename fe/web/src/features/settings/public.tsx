import { Button as AstryxButton } from '@astryxdesign/core/Button';
import { Divider as AstryxDivider } from '@astryxdesign/core/Divider';
import { Heading as AstryxHeading } from '@astryxdesign/core/Heading';
import { IconButton as AstryxIconButton } from '@astryxdesign/core/IconButton';
import { MetadataList, MetadataListItem } from '@astryxdesign/core/MetadataList';
import { SideNav as AstryxSideNav, SideNavItem as AstryxSideNavItem } from '@astryxdesign/core/SideNav';
import { VisuallyHidden } from '@astryxdesign/core/VisuallyHidden';
import { useEffect, type ReactNode } from 'react';

import { upgradeReceiptSummary, type SystemUpgradeState } from '../../../../core/domain/system-upgrade.ts';
import { Icon } from '../../ui/icon/public.tsx';
import { writeClipboardText as copyText } from '../../ui/operation-feedback/clipboard.ts';
import { useState } from '../../ui/state/public.ts';
import { bindSettingField, SettingFieldsList, type SegmentedSettingField } from './fields.tsx';
import {
  SETTINGS_SECTIONS, SettingsIndex, type SettingsPresentation, type SettingsSection, type SettingsSectionEntry,
} from './navigation.tsx';
import { SettingsPane, SettingStatus } from './pane.tsx';
import styles from './settings.module.css';

export type SettingsSurfaceProps = Readonly<{
  presentation: SettingsPresentation;
  section: SettingsSection;
  sections?: readonly SettingsSectionEntry[];
  onSelectSection: (section: SettingsSection) => void;
  onClose?: () => void;
  children: ReactNode;
}>;

/** The two-column frame every Settings route renders inside. `aria-current="page"` is stamped on top of `isSelected`: the current route is a fact a screen reader has to be told. */
export function SettingsSurface({ presentation, section, sections = SETTINGS_SECTIONS, onSelectSection, onClose, children }: SettingsSurfaceProps) {
  return (
    <div className={`${styles.surface} ${presentation === 'desktop' ? '' : styles.mobileSurface} ${presentation === 'mobile-detail' ? styles.mobileDetail : ''}`}>
      {presentation === 'mobile-index' && <SettingsIndex sections={sections} onSelectSection={onSelectSection} />}
      {presentation === 'desktop' && <AstryxSideNav aria-label="Settings sections" className={styles.sectionNav}>
        {sections.map((entry) => (
          <AstryxSideNavItem key={entry.id} label={entry.label} icon={<Icon name={entry.icon} size="sm" />}
            isSelected={entry.id === section} aria-current={entry.id === section ? 'page' : undefined}
            onClick={() => onSelectSection(entry.id)} />
        ))}
      </AstryxSideNav>}
      {presentation === 'desktop' && onClose !== undefined && (
        <AstryxIconButton className={styles.close} icon={<Icon name="close" size="sm" />}
          label="Close" variant="ghost" size="sm" onClick={onClose} />
      )}
      <div className={styles.pane} hidden={presentation === 'mobile-index'}>
        {children}
      </div>
    </div>
  );
}

const THEME_FIELD = Object.freeze({
  id: 'theme',
  label: 'Theme',
  description: "System follows your operating system's appearance.",
  control: 'segmented',
  options: Object.freeze([
    Object.freeze({ value: 'light', label: 'Light', icon: 'sun' }),
    Object.freeze({ value: 'dark', label: 'Dark', icon: 'moon' }),
    Object.freeze({ value: 'system', label: 'System', icon: 'monitor' }),
  ] as const),
} as const satisfies SegmentedSettingField);

export type ThemeMode = (typeof THEME_FIELD.options)[number]['value'];

export function AppearancePane({ themeMode, onThemeModeChange }: Readonly<{
  themeMode: ThemeMode;
  onThemeModeChange: (mode: ThemeMode) => void;
}>) {
  return (
    <SettingsPane category="appearance">
      <SettingFieldsList fields={[bindSettingField(THEME_FIELD, { value: themeMode, onChange: onThemeModeChange })]} />
    </SettingsPane>
  );
}

export function AboutPane({ system }: Readonly<{ system?: SystemUpgradeState | null }> = {}) {
  const [copyStatus, setCopyStatus] = useState<'idle' | 'copying' | 'copied' | 'failed'>('idle');
  useEffect(() => {
    if (copyStatus !== 'copied') return;
    const timer = window.setTimeout(() => setCopyStatus('idle'), 2000);
    return () => window.clearTimeout(timer);
  }, [copyStatus, setCopyStatus]);

  const build = [
    ['Version', __NC_VERSION__],
    ['Build', __NC_BUILD__],
    ...(system ? [
      ['Release', system.info?.releaseId ?? 'Loading…'],
      ['Web build', system.info?.webBuildId ?? 'Loading…'],
      ['Host build', system.info?.hostBuildId ?? 'Loading…'],
    ] : []),
  ];
  const runtime: readonly (readonly [label: string, value: string, variant?: 'warning' | 'success'])[] = system ? [
    ['Schema version', system.info ? String(system.info.schemaVersion) : 'Loading…'],
    ['Status', system.draining ? 'Draining' : 'Active', system.draining ? 'warning' : 'success'],
    ['Active dispatches', String(system.activeDispatches)],
    ...(system.receipt ? [['Latest upgrade receipt', upgradeReceiptSummary(system.receipt)] as const] : []),
  ] : [];

  async function copyDiagnostics() {
    setCopyStatus('copying');
    const lines = [...build, ...runtime].map(([label, value]) => `${label}: ${value}`);
    try {
      await copyText(['Perga', ...lines].join('\n'));
      setCopyStatus('copied');
    } catch {
      setCopyStatus('failed');
    }
  }

  const copyLabel = copyStatus === 'copied' ? 'Copied' : 'Copy diagnostics';
  return (
    <SettingsPane category="about">
      <div className={styles.colophon}>
        <span className={styles.brandMark} aria-hidden="true" />
        <div className={styles.brandIdentity}>
          <span className={styles.brandName}>Perga</span>
          <span className={styles.brandVersion}>{__NC_VERSION__}</span>
        </div>
        <AstryxButton className={styles.copyDiagnostics} label={copyLabel} variant="secondary" size="sm"
          icon={<Icon name={copyStatus === 'copied' ? 'check' : 'copy'} size="sm" />}
          isLoading={copyStatus === 'copying'} onClick={() => { void copyDiagnostics(); }}>
          {copyLabel}
        </AstryxButton>
        <VisuallyHidden role="status">{copyStatus === 'copied' ? 'Diagnostics copied.' : ''}</VisuallyHidden>
      </div>
      {copyStatus === 'failed' && <p className={styles.error} role="alert">Could not copy diagnostics.</p>}
      <AstryxDivider />
      <MetadataList columns="single" className={styles.metadata}
        title={<AstryxHeading level={4} className={styles.eyebrow}>Build</AstryxHeading>}>
        {build.map(([label, value]) => <MetadataListItem key={label} label={label}>{value}</MetadataListItem>)}
      </MetadataList>
      {system && (
        <MetadataList columns="single" className={styles.metadata}
          title={<AstryxHeading level={4} className={styles.eyebrow}>Runtime</AstryxHeading>}>
          {runtime.map(([label, value, variant]) => (
            <MetadataListItem key={label} label={label}>
              {variant !== undefined ? <SettingStatus label={value} variant={variant} /> : value}
            </MetadataListItem>
          ))}
        </MetadataList>
      )}
    </SettingsPane>
  );
}
