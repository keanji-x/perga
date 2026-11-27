import { Button as AstryxButton } from '@astryxdesign/core/Button';
import { Heading as AstryxHeading } from '@astryxdesign/core/Heading';
import { IconButton as AstryxIconButton } from '@astryxdesign/core/IconButton';
import { Text as AstryxText } from '@astryxdesign/core/Text';
import { Tooltip as AstryxTooltip } from '@astryxdesign/core/Tooltip';

import type { SettingsSnapshotDto } from '../../../../core/api/generated/wire.ts';
import type { AgentProvider } from '../../../../core/api/schemas.ts';
import {
  unavailableCreationNote, type ProviderAvailability, type ManagedProviderAvailability, type AuthenticationRetryResponse,
} from '../../../../core/domain/agent-providers.ts';
import { providerPresentation } from '../../../../core/domain/provider-presentation.ts';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { Icon } from '../../ui/icon/public.tsx';
import { bindSettingsSection, SettingFieldsList } from './fields.tsx';
import { SettingRow, SettingsList, SettingsPane, SettingStatus } from './pane.tsx';
import styles from './settings.module.css';

export type PlannersPaneProps = Readonly<{
  settings?: PlannerDefaultsProps;
  /** `undefined` means "still checking" — never render a guessed status. */
  providers: readonly PlannerProvider[] | undefined;
  loadError: string | null;
  onRetryLoad: () => void;
  /** Run every check again now (`refresh=true`). */
  onRecheck: () => void;
  rechecking: boolean;
  /** Why the last Recheck failed; the previous answer stays on screen. */
  recheckError: string | null;
  /** An app-injected capability; the pane never infers a provider's recovery policy. */
  authenticationRecovery: Readonly<{
    provider: AgentProvider;
    pending: boolean;
    error: string | null;
    notices: AuthenticationRetryResponse['recovery_notices'];
    onRetry: (revision: string) => void;
    onOpenConversation: (notice: AuthenticationRetryResponse['recovery_notices'][number]) => void;
  }> | null;
}>;

type PlannerDefaultsProps = Readonly<{
  snapshot: SettingsSnapshotDto | undefined;
  loadError: string | null;
  saveError: string | null;
  saving: boolean;
  onChange: (key: string, value: string | number) => void;
  onRetry: () => void;
}>;

function PlannerDefaults({ snapshot, loadError, saveError, saving, onChange, onRetry }: PlannerDefaultsProps) {
  const fields = snapshot === undefined ? [] : bindSettingsSection(snapshot, 'planners', onChange);
  if (snapshot !== undefined && fields.length === 0 && loadError === null) return null;

  return (
    <section className={styles.settingsSection} aria-label="Defaults" aria-busy={saving || undefined}>
      <header className={styles.sectionHeader}>
        <AstryxHeading level={4} className={styles.eyebrow}>Defaults</AstryxHeading>
      </header>
      {loadError !== null ? <ErrorBox message={loadError} actionLabel="Retry defaults" onRetry={onRetry} />
        : snapshot === undefined ? <AstryxText as="p" color="secondary">Loading planner defaults…</AstryxText>
          : <SettingFieldsList fields={fields} disabled={saving} density="compact" />}
      {loadError === null && saveError !== null && <p className={styles.error} role="alert">{saveError}</p>}
    </section>
  );
}

/** Availability and the one required capability consumed by Settings. */
type PlannerProvider = Readonly<{
  availability: ProviderAvailability;
  accepts_unavailable_blank_track: ManagedProviderAvailability['accepts_unavailable_blank_track'];
}>;

const STATUS_META: Readonly<Record<ProviderAvailability['status'], Readonly<{
  label: string; variant: 'success' | 'warning' | 'neutral';
}>>> = Object.freeze({
  ready: Object.freeze({ label: 'Ready', variant: 'success' }),
  unavailable: Object.freeze({ label: 'Unavailable', variant: 'warning' }),
  not_configured: Object.freeze({ label: 'Not configured', variant: 'neutral' }),
});

/** The server's reason and its blank-creation advice; `null` when ready with nothing extra to say. */
function describe({ availability: entry, accepts_unavailable_blank_track }: PlannerProvider): string | null {
  if (entry.authentication_notice !== null) return entry.authentication_notice.text;
  if (entry.reason === null) return null;
  return entry.status === 'unavailable' && accepts_unavailable_blank_track
    ? `${entry.reason} ${unavailableCreationNote(providerPresentation(entry.provider).label)}`
    : entry.reason;
}

/** The oldest answer on screen: what "last checked" can honestly claim for the whole list. */
function oldestCheck(providers: readonly ProviderAvailability[]): number | null {
  return providers.reduce<number | null>(
    (oldest, entry) => oldest === null || entry.checked_at_ms < oldest ? entry.checked_at_ms : oldest, null);
}

export function PlannersPane({
  settings, providers, loadError, onRetryLoad, onRecheck, rechecking, recheckError, authenticationRecovery,
}: PlannersPaneProps) {
  const checkedAt = providers === undefined ? null : oldestCheck(providers.map(({ availability }) => availability));
  return (
    <SettingsPane category="planners">
      {settings !== undefined && <PlannerDefaults {...settings} />}
      {loadError !== null && <ErrorBox message={loadError} onRetry={onRetryLoad} />}
      {providers === undefined
        ? loadError === null && <AstryxText as="p" color="secondary">Checking providers…</AstryxText>
        : (
          <section className={styles.settingsSection} aria-label="Providers">
            <header className={styles.sectionHeader}>
              <div className={styles.sectionHeading}>
                <AstryxHeading level={4} className={styles.eyebrow}>Providers</AstryxHeading>
                <AstryxTooltip content="Recheck planners" placement="above">
                  <AstryxIconButton className={styles.recheckButton} label="Recheck planners" variant="ghost" size="sm"
                    isLoading={rechecking} icon={<Icon name="refresh" size="sm" />} onClick={onRecheck} />
                </AstryxTooltip>
              </div>
              {checkedAt !== null && <span className={styles.checkTime}>
                Last checked at {new Date(checkedAt).toLocaleTimeString()}
              </span>}
            </header>
            <SettingsList density="compact" className={styles.providerList}>
              {providers.map(({ availability: entry, accepts_unavailable_blank_track }) => {
                const notice = entry.authentication_notice;
                const detail = describe({ availability: entry, accepts_unavailable_blank_track });
                const recovery = authenticationRecovery?.provider === entry.provider && notice?.kind === 'sign_in_required'
                  ? <AstryxButton label="Retry queued messages after server sign-in" variant="secondary" size="sm"
                    isLoading={authenticationRecovery.pending} onClick={() => authenticationRecovery.onRetry(notice.revision)}>
                    Retry after server sign-in
                  </AstryxButton>
                  : null;
                const label = providerPresentation(entry.provider).label;
                const authText = entry.authentication_method !== null ? `Authentication: ${entry.authentication_method}` : null;
                const inlineMeta = authText !== null || detail !== null
                  ? <span className={styles.plannerInlineMeta}>
                    {authText !== null && <AstryxText className={styles.authenticationMethod} type="inherit" color="inherit"
                      tabIndex={0} maxLines={1} hasTruncateTooltip="below">{authText}</AstryxText>}
                    {detail !== null && <AstryxText className={styles.plannerNote} type="inherit" color="inherit"
                      tabIndex={0} maxLines={1} hasTruncateTooltip="below">{detail}</AstryxText>}
                  </span>
                  : null;
                return (
                  <SettingRow
                    key={entry.provider}
                    title={<span className={styles.providerHeadline}>
                      <span className={styles.providerName}>{label}</span>
                      <SettingStatus label={STATUS_META[entry.status].label} variant={STATUS_META[entry.status].variant} />
                      {inlineMeta}
                    </span>}
                    startContent={<span className={styles.providerIcon}><Icon size="sm"
                      name={entry.provider === 'claude' ? 'claude' : entry.provider === 'codex' ? 'codex' : 'agent'} /></span>}
                    stackControlOnMobile={recovery !== null}
                    control={recovery}
                  />
                );
              })}
            </SettingsList>
          </section>
        )}
      {authenticationRecovery?.error !== null && authenticationRecovery?.error !== undefined
        && <p className={styles.error} role="alert">{authenticationRecovery.error}</p>}
      {authenticationRecovery?.notices.map((notice) => <ErrorBox key={notice.card_id}
        message={`${notice.title}: ${notice.text}`} actionLabel="Open conversation"
        onRetry={() => authenticationRecovery.onOpenConversation(notice)} />)}
      {recheckError !== null && <p className={styles.error} role="alert">{recheckError}</p>}
    </SettingsPane>
  );
}
