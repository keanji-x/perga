import { useSystemUpgrade } from '../providers/system-upgrade.tsx';
// One Settings visit: a desktop dialog or a mobile route page, mounted above the
// shell's keyed route stage so section routes keep one surface. The URL selects the section.

import { useQuery } from '@tanstack/react-query';
import { useRouter,useRouterState,type RouterHistory } from '@tanstack/react-router';
import { useCallback,useEffect,useRef } from 'react';
import { createPortal } from 'react-dom';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import {
SETTINGS_SECTIONS,
settingsSectionLabel,type SettingsSection
} from '../../features/settings/navigation.tsx';
import { PlannersPane } from '../../features/settings/planners.tsx';
import {
AboutPane,AppearancePane,
SettingsSurface,
type ThemeMode as SettingsThemeMode
} from '../../features/settings/public.tsx';
import { Dialog } from '../../ui/dialog/public.tsx';
import { MobileHeader } from '../../ui/mobile-header/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { useCompactViewport } from '../../ui/viewport/public.ts';
import { agentProvidersQueryOptions,useAgentProvidersRecheck } from '../providers/agent-providers.ts';
import { managedProvidersQueryOptions } from '../providers/managed-providers.ts';
import { useUiPreferences } from '../providers/ui-preferences.tsx';
import { useCurrentPath,useGo,type NavTarget } from '../router/navigation.ts';
import { useTheme } from '../theme/public.tsx';
import styles from './settings-page.module.css';

/** Which pane the path asks for, or `null` when the reader is not in Settings. */
export function settingsSectionForPath(path: string,): SettingsSection | null {

    if (path === '/settings') return 'appearance';
    const found = SETTINGS_SECTIONS.find((entry) => path === `/settings/${entry.id}`)?.id ?? null;
    if (found === null) return null;
    return SETTINGS_SECTIONS.some((entry) => entry.id === found) ? found : 'appearance';

}

/** Desktop Network keeps the historical root; every mobile category has a URL. */
function targetForSection(section: SettingsSection, compact: boolean,): NavTarget {
  const defaultSection: SettingsSection = 'appearance';
  return { name: section === defaultSection && !compact ? 'settings' : `settings-${section}` };
}

export type SettingsOverlayProps = Readonly<{
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;

}>;

/**
 * The pane tree is one portal into a stable container; only the container's DOM
 * parent changes between the mobile page and the desktop dialog slot, so resizing
 * cannot discard plugin drafts. DOM ancestry is not React context here.
 */
export function SettingsOverlay({ transport, unauthorized, }: SettingsOverlayProps) {
  const go = useGo();
  const router = useRouter();
  const path = useCurrentPath();
  const href = useRouterState({ select: (state) => state.location.href });
  const section = settingsSectionForPath(path);
  const compact = useCompactViewport();
  const mobileIndex = compact && path === '/settings';
  const [contentHost] = useState(() => document.createElement('div'));
  const focusedContentRef = useRef<HTMLElement | null>(null);
  const relocatingContentRef = useRef(false);
  const attachContent = useCallback((slot: HTMLDivElement | null) => {
    if (slot === null || contentHost.parentNode === slot) return;
    const focused = focusedContentRef.current;
    const restore = focused !== null && contentHost.contains(focused)
      && (document.activeElement === focused || document.activeElement === document.body);
    // Moving the same editor between responsive hosts is not leaving its field.
    relocatingContentRef.current = true;
    slot.appendChild(contentHost);
    if (restore) focused.focus({ preventScroll: true });
    relocatingContentRef.current = false;
  }, [contentHost]);
  useEffect(() => {
    // On desktop → phone, Dialog removes background inertness in its cleanup.
    // A focus attempt while moving the field can therefore have been ignored.
    const focused = focusedContentRef.current;
    if (compact && focused !== null && contentHost.contains(focused)
      && document.activeElement === document.body) focused.focus({ preventScroll: true });
  }, [compact, contentHost]);
  const history = router.history as RouterHistory;
  const historyIndex = history.location.state.__TSR_index;
  const returnLocation = useRef<Readonly<{ href: string; index: number }> | null>(null);
  const indexLocation = useRef<number | null>(null);
  useEffect(() => {
    if (section === null) {
      returnLocation.current = { href, index: historyIndex };
      indexLocation.current = null;
    } else if (path === '/settings') indexLocation.current = historyIndex;
  }, [href, historyIndex, path, section]);
  const leaveSettings = () => {
    const previous = returnLocation.current;
    const distance = previous === null ? 0 : history.location.state.__TSR_index - previous.index;
    // Only pop entries after a workspace entry this mounted shell observed.
    // Section pushes made on desktop belong to the same visit if resized.
    if (distance > 0 && history.canGoBack()) {
      history.go(-distance);
      return;
    }
    // A cold settings link, or a replaced workspace entry, has no owned
    // entry to pop. Keep that exit inside the app without guessing at Back.
    void router.navigate({ to: previous?.href ?? '/', replace: true });
  };
  const backToIndex = () => {
    const previous = indexLocation.current;
    const distance = previous === null ? 0 : history.location.state.__TSR_index - previous;
    if (distance > 0 && history.canGoBack()) {
      history.go(-distance);
      return;
    }
    // Cold category links and desktop shortcuts have no observed index entry.
    go({ name: 'settings' }, { replace: true });
  };
  if (section === null) return null;
  return <>
    <section className={styles.page} hidden={!compact} data-nc-settings-page="" aria-label="Settings">
      <MobileHeader title={mobileIndex ? 'Settings' : settingsSectionLabel(section)} level={1}
        backLabel={mobileIndex ? 'workspace' : 'Settings'} onBack={mobileIndex ? leaveSettings : backToIndex} />
      <div className={styles.content} ref={compact ? attachContent : undefined} />
    </section>
    <Dialog purpose="form" origin={{ kind: 'surface-center' }} open={!compact} onClose={leaveSettings} title="Settings" wide>
      <div ref={!compact ? attachContent : undefined} />
    </Dialog>
    {createPortal(<div
      onFocusCapture={(event) => { focusedContentRef.current = event.target; }}
      onBlurCapture={(event) => {
        if (relocatingContentRef.current) event.stopPropagation();
        else if (!contentHost.contains(event.relatedTarget)) focusedContentRef.current = null;
      }}>
    <SettingsSurface section={section}
      sections={SETTINGS_SECTIONS}
      presentation={!compact ? 'desktop' : mobileIndex ? 'mobile-index' : 'mobile-detail'}
      onSelectSection={(next) => go(targetForSection(next, compact))}>
      <SectionPane section={section} transport={transport} unauthorized={unauthorized} />
    </SettingsSurface></div>, contentHost)}
  </>;
}

/** One switch, so a new section cannot forget to be rendered. */
function SectionPane({ section, transport, unauthorized, }: SettingsOverlayProps & {
  section: SettingsSection;
}) {
  const system = useSystemUpgrade();
  switch (section) {
    case 'appearance': return <AppearancePaneHost />;
    case 'planners': return <PergaPlannersPaneHost transport={transport} unauthorized={unauthorized} />;
    case 'about': return <AboutPane system={system} />;
  }
}

function PergaPlannersPaneHost({ transport, unauthorized }: SettingsOverlayProps) {
  const preferences = useUiPreferences();
  const [readRevision, setReadRevision] = useState(0);
  const savedMinutes = preferences.turnDurationMinutes();
  const providers = useQuery(managedProvidersQueryOptions(transport, unauthorized));
  const availability = useQuery(agentProvidersQueryOptions(transport, unauthorized));
  const missing = availability.data?.find((entry) => providers.data !== undefined
    && !providers.data.some(({ availability: managed }) => managed.provider === entry.provider));
  const joined = availability.data === undefined || providers.data === undefined || missing !== undefined
    ? undefined : availability.data.map((entry) => ({
      availability: entry,
      accepts_unavailable_blank_track: providers.data.find(({ availability: managed }) => managed.provider === entry.provider)!.accepts_unavailable_blank_track,
    }));
  const loadError = availability.error ?? providers.error;
  const recheck = useAgentProvidersRecheck(transport, unauthorized);
  return (
    <PlannersPane
      turnDuration={{
        loaded: true,
        readRevision,
        loadError: null,
        onRetryLoad: () => { setReadRevision((n) => n + 1); },
        value: savedMinutes === null ? undefined : String(savedMinutes),
        onSave: (value) => {
          const parsed = value === undefined ? null : Number(value);
          preferences.setTurnDurationMinutes(parsed !== null && Number.isInteger(parsed) ? parsed : null);
          setReadRevision((n) => n + 1);
          return Promise.resolve();
        },
      }}
      authenticationRecovery={null}
      providers={joined}
      loadError={loadError !== null ? readErrorText(loadError, 'Planners are unavailable.')
        : missing === undefined ? null : `Planner capabilities are unavailable for ${missing.provider}.`}
      onRetryLoad={() => { void availability.refetch(); void providers.refetch(); }}
      onRecheck={() => { recheck.recheck(); void providers.refetch(); }}
      rechecking={recheck.rechecking}
      recheckError={recheck.error}
    />
  );
}

function AppearancePaneHost() {
  const theme = useTheme();
  return (
    <AppearancePane
      // `app/theme` and `features/settings` each own their copy of the mode union
      // (features may not import app); the two are only kept in step by this line.
      themeMode={theme.mode satisfies SettingsThemeMode}
      onThemeModeChange={(mode) => theme.setMode(mode)}
    />
  );
}
