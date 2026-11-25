import { Button } from '@astryxdesign/core/Button';
import { Popover } from '@astryxdesign/core/Popover';
import { Tooltip } from '@astryxdesign/core/Tooltip';
import { upgradeReceiptSummary, type SystemUpgradeState } from '../../../../core/domain/system-upgrade.ts';
import { Icon } from '../../ui/icon/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { useSystemUpgrade } from '../providers/system-upgrade.tsx';
import { useConnectionStatus, useSystemUpgradeStore } from '../providers/connection-status.tsx';
import styles from './system-upgrade-banner.module.css';

export type SystemUpgradeVariant = 'sidebar' | 'collapsed' | 'floating';

function headlineFor(state: SystemUpgradeState, receiptStatus: string | null): string {
  if (receiptStatus === 'rolled-back') return 'Upgrade rolled back';
  if (receiptStatus === 'failed') return 'Upgrade failed';
  if (state.draining) return 'Upgrading';
  if (state.webRefreshAvailable) return 'Update available';
  return 'Upgrade complete';
}

export function SystemUpgradeBanner({ state, connected, onRefresh, onDismiss, variant = 'sidebar' }: Readonly<{
  state: SystemUpgradeState | null;
  connected: boolean;
  onRefresh: () => void;
  onDismiss: () => void;
  variant?: SystemUpgradeVariant;
}>) {
  const [open, setOpen] = useState(false);
  const popoverOpen = variant === 'collapsed' && open;
  if (!state) return null;
  const receipt = state.receipt?.jobId !== state.dismissedReceipt ? state.receipt : null;
  if (!state.draining && !state.webRefreshAvailable && !receipt) return null;
  const tone = receipt?.status === 'rolled-back' || receipt?.status === 'failed'
    ? 'error' : state.draining ? 'warning' : 'accent';
  const headline = headlineFor(state, receipt?.status ?? null);
  const hasActions = state.webRefreshAvailable || receipt !== null;

  const content = (
    <div className={styles.inner}>
      <div className={styles.head}>
        <span className={`${styles.dot} ${styles[tone]}`} aria-hidden="true" />
        <span className={styles.title}>{headline}</span>
      </div>
      <div className={styles.messages} role="status">
        {state.draining && <p>Upgrade draining · {state.activeDispatches} active dispatches</p>}
        {state.draining && !connected && <p>Reconnecting during upgrade…</p>}
        {state.webRefreshAvailable && <p>A web update is available. Your unsent text stays here until you refresh.</p>}
        {receipt && <p>{upgradeReceiptSummary(receipt)}</p>}
      </div>
      {hasActions && (
        <div className={styles.actions}>
          {state.webRefreshAvailable && (
            <Button size="sm" variant="primary" label="Refresh" onClick={() => { setOpen(false); onRefresh(); }} />
          )}
          {receipt && (
            <Button size="sm" variant="ghost" label="Dismiss upgrade receipt" onClick={() => { setOpen(false); onDismiss(); }}>
              Dismiss
            </Button>
          )}
        </div>
      )}
    </div>
  );

  if (variant === 'collapsed') {
    return (
      <aside className={styles.collapsedWrap} aria-label="System upgrade">
        <Popover
          label="System upgrade"
          isOpen={popoverOpen}
          onOpenChange={setOpen}
          width="min(18rem, calc(100vw - 2rem))"
          placement="below"
          alignment="start"
          content={<div className={styles.popoverCard}>{content}</div>}
        >
          {(trigger) => (
            <Tooltip content={headline} placement="end">
              <button
                {...trigger}
                type="button"
                className={styles.collapsedTrigger}
                aria-label={`System upgrade: ${headline}`}
              >
                <Icon name="arrow-up" size="sm" />
                <span className={`${styles.cornerDot} ${styles[tone]}`} aria-hidden="true" />
              </button>
            </Tooltip>
          )}
        </Popover>
      </aside>
    );
  }

  return (
    <aside
      className={`${styles.card} ${variant === 'floating' ? styles.floating : styles.sidebar}`}
      aria-label="System upgrade"
    >
      {content}
    </aside>
  );
}

export function SystemUpgradeBannerHost({ variant = 'sidebar' }: Readonly<{ variant?: SystemUpgradeVariant }>) {
  const state = useSystemUpgrade();
  const store = useSystemUpgradeStore();
  const connection = useConnectionStatus();
  return <SystemUpgradeBanner state={state} connected={connection?.connected ?? false} variant={variant}
    onRefresh={() => window.location.reload()} onDismiss={() => store?.dismissReceipt()} />;
}
