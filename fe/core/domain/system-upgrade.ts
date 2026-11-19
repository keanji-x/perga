import type { ApiOperation } from '../api/types.js';
import { systemInfoDtoSchema, type SystemInfoDto, type SystemUpgradeReceipt, type SystemUpgradeStatusPayload } from '../api/perga-v1.js';

export function systemInfoOperation(): ApiOperation<SystemInfoDto> {
  return { method: 'GET', path: '/api/v1/system/info', responseSchema: systemInfoDtoSchema };
}

export type SystemUpgradeState = Readonly<{
  info: SystemInfoDto | null;
  baselineWebBuildId: string | null;
  webRefreshAvailable: boolean;
  draining: boolean;
  activeDispatches: number;
  receipt: SystemUpgradeReceipt | null;
  dismissedReceipt: string | null;
}>;

/** One in-memory baseline per loaded page. No reload, persistence, or platform effects. */
export class SystemUpgradeStore {
  private state: SystemUpgradeState = {
    info: null, baselineWebBuildId: null, webRefreshAvailable: false,
    draining: false, activeDispatches: 0, receipt: null, dismissedReceipt: null,
  };
  private statusRevision = 0;
  private readonly listeners = new Set<() => void>();
  readonly read = (): SystemUpgradeState => this.state;
  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };
  /** Capture before requesting info so an older HTTP response cannot undo a newer WS status. */
  revision(): number { return this.statusRevision; }
  acceptInfo(info: SystemInfoDto, revision = this.statusRevision): void {
    const baseline = this.state.baselineWebBuildId ?? (info.webBuildId || null);
    const status = revision === this.statusRevision ? info : this.state;
    this.update({ ...this.state, info: { ...info, draining: status.draining, activeDispatches: status.activeDispatches },
      baselineWebBuildId: baseline,
      webRefreshAvailable: baseline !== null && info.webBuildId !== '' && info.webBuildId !== baseline,
      draining: status.draining, activeDispatches: status.activeDispatches });
  }
  acceptStatus(status: SystemUpgradeStatusPayload): void {
    this.statusRevision++;
    const receipt = status.receipt;
    // Replayed/older receipts cannot replace the latest observation or reopen its dismissal.
    const latest = receipt && (!this.state.receipt || receipt.finishedAt >= this.state.receipt.finishedAt)
      ? receipt : this.state.receipt;
    this.update({ ...this.state, draining: status.draining, activeDispatches: status.activeDispatches,
      info: this.state.info && { ...this.state.info, draining: status.draining, activeDispatches: status.activeDispatches },
      receipt: latest });
  }
  dismissReceipt(): void {
    this.update({ ...this.state, dismissedReceipt: this.state.receipt?.jobId ?? null });
  }
  private update(state: SystemUpgradeState): void {
    this.state = state;
    for (const listener of this.listeners) listener();
  }
}

export function upgradeReceiptSummary(receipt: SystemUpgradeReceipt): string {
  return `Upgrade ${receipt.status}: ${receipt.oldReleaseId ?? 'unknown'} → ${receipt.newReleaseId ?? 'unknown'}`
    + (receipt.error ? ` · ${receipt.error}` : '')
    + (receipt.rollbackError ? ` · Rollback: ${receipt.rollbackError}` : '');
}
