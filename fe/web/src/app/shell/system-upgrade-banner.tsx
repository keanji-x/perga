import { upgradeReceiptSummary, type SystemUpgradeState } from '../../../../core/domain/system-upgrade.ts';
import { useSystemUpgrade } from '../providers/system-upgrade.tsx';
import { useConnectionStatus, useSystemUpgradeStore } from '../providers/connection-status.tsx';
import styles from './system-upgrade-banner.module.css';

export function SystemUpgradeBanner({ state, connected, onRefresh, onDismiss }: Readonly<{
  state: SystemUpgradeState | null; connected: boolean; onRefresh: () => void; onDismiss: () => void;
}>) {
  if (!state) return null;
  const receipt = state.receipt?.jobId !== state.dismissedReceipt ? state.receipt : null;
  if (!state.draining && !state.webRefreshAvailable && !receipt) return null;
  return <aside className={styles.banner} aria-label="System upgrade">
    <div role="status">
      {state.draining && <p>Upgrade draining · {state.activeDispatches} active dispatches</p>}
      {state.draining && !connected && <p>Reconnecting during upgrade…</p>}
      {state.webRefreshAvailable && <p>A web update is available. Your unsent text stays here until you refresh.</p>}
      {receipt && <p>{upgradeReceiptSummary(receipt)}</p>}
    </div>
    <div className={styles.actions}>
      {state.webRefreshAvailable && <button type="button" onClick={onRefresh}>Refresh</button>}
      {receipt && <button type="button" aria-label="Dismiss upgrade receipt" onClick={onDismiss}>Dismiss</button>}
    </div>
  </aside>;
}

export function SystemUpgradeBannerHost() {
  const state = useSystemUpgrade();
  const store = useSystemUpgradeStore();
  const connection = useConnectionStatus();
  return <SystemUpgradeBanner state={state} connected={connection?.connected ?? false}
    onRefresh={() => window.location.reload()} onDismiss={() => store?.dismissReceipt()} />;
}
