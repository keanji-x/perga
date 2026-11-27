import { Heading as AstryxHeading } from '@astryxdesign/core/Heading';
import { List as AstryxList, ListItem as AstryxListItem } from '@astryxdesign/core/List';
import { StatusDot } from '@astryxdesign/core/StatusDot';
import { Text as AstryxText } from '@astryxdesign/core/Text';
import type { ReactNode } from 'react';

import { settingsSection, type SettingsSection } from './navigation.tsx';
import styles from './settings.module.css';

/** A pane: its heading, one sentence saying what the group is for, and its rows. */
export function SettingsPane({ category, lede: ledeOverride, children }: Readonly<{
  category: SettingsSection;
  lede?: string;
  children: ReactNode;
}>) {
  const { label, lede: defaultLede } = settingsSection(category);
  const lede = ledeOverride ?? defaultLede;
  const headingId = `nc-settings-${category}`;
  return (
    <section className={styles.group} aria-labelledby={headingId}>
      <header className={styles.paneHeader}>
        <AstryxHeading level={3} id={headingId} className={styles.categoryHeading}>{label}</AstryxHeading>
        {lede !== undefined && <AstryxText as="p" color="secondary">{lede}</AstryxText>}
      </header>
      {children}
    </section>
  );
}

/** The rows of a pane. Hairlines between them and nothing else. */
export function SettingsList({ children, density = 'balanced', className }: Readonly<{
  children: ReactNode;
  density?: 'compact' | 'balanced';
  className?: string;
}>) {
  return <AstryxList hasDividers density={density} className={className === undefined ? styles.list : `${styles.list} ${className}`}>{children}</AstryxList>;
}

/** Inline status mark with an official StatusDot shared across Planners and About. */
export function SettingStatus({ label, variant }: Readonly<{
  label: string;
  variant: 'success' | 'warning' | 'neutral';
}>) {
  return (
    <span className={styles.statusIndicator} data-nc-status-tone={variant}>
      <StatusDot className={styles.statusDot} variant={variant} label={label} aria-hidden="true" />
      <span>{label}</span>
    </span>
  );
}

export function SettingRow({
  title, description, startContent, control, stackControlOnMobile = true,
}: Readonly<{
  title: ReactNode;
  /** One sentence. Omitted when the title already says everything. */
  description?: ReactNode;
  /** An icon or other visual before the title. */
  startContent?: ReactNode;
  control: ReactNode;
  stackControlOnMobile?: boolean;
}>) {
  return (
    <AstryxListItem
      className={`${styles.row} ${stackControlOnMobile ? styles.controlRow : ''}`}
      label={title}
      description={description}
      startContent={startContent}
      endContent={control}
    />
  );
}
