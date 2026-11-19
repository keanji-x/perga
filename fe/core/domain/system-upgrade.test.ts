import { describe, expect, it } from 'vitest';
import { SystemUpgradeStore, systemInfoOperation } from './system-upgrade.js';
import { decodeEventFrame } from '../events/protocol.js';
import { performApiRequest } from '../api/client.js';
import type { SystemInfoDto, SystemUpgradeReceipt } from '../api/perga-v1.js';

const info: SystemInfoDto = Object.freeze({ releaseId: 'release-1', webBuildId: 'web-1', hostBuildId: 'host-1', bridgeBuildId: 'bridge-1', schemaVersion: 11, draining: false, activeDispatches: 0 });
const receipt: SystemUpgradeReceipt = Object.freeze({ jobId: 'job-1', oldReleaseId: 'release-1', newReleaseId: 'release-2', status: 'rolled-back', error: 'Health check failed', rollbackError: null, notifyTrackId: null, finishedAt: 100, changed: ['host'] });

describe('system upgrade store', () => {
  it('decodes system info through the production operation', async () => {
    const result = await performApiRequest({ send: request => {
      expect(request.path).toBe('/api/v1/system/info');
      expect(request.method).toBe('GET');
      return Promise.resolve({ status: 200, statusText: 'OK', body: info });
    } }, systemInfoOperation());
    expect(result).toEqual({ status: 'ready', value: info });
  });
  it('compares a stable non-empty web baseline, never release identity', () => {
    const store = new SystemUpgradeStore();
    store.acceptInfo({ ...info, webBuildId: '' });
    expect(store.read().baselineWebBuildId).toBeNull();
    store.acceptInfo(info);
    store.acceptInfo({ ...info, releaseId: 'release-2' });
    expect(store.read().webRefreshAvailable).toBe(false);
    store.acceptInfo({ ...info, webBuildId: 'web-2' });
    store.acceptInfo({ ...info, webBuildId: 'web-2' });
    expect(store.read().webRefreshAvailable).toBe(true);
    expect(store.read().baselineWebBuildId).toBe('web-1');
    store.acceptInfo(info); // rollback to the loaded build
    expect(store.read().webRefreshAvailable).toBe(false);
    store.acceptInfo({ ...info, webBuildId: '' });
    expect(store.read().webRefreshAvailable).toBe(false);
    const refreshed = new SystemUpgradeStore();
    refreshed.acceptInfo({ ...info, webBuildId: 'web-2' });
    expect(refreshed.read().webRefreshAvailable).toBe(false);
  });
  it('accepts actual transient WS frames, retains dismissed receipts, and fences stale HTTP drain values', () => {
    const store = new SystemUpgradeStore();
    store.acceptInfo(info);
    const revision = store.revision();
    const decoded = decodeEventFrame({ kind: 'system.upgrade_status', payload: { draining: true, activeDispatches: 2, receipt }, eventVersion: 1 });
    expect(decoded.status).toBe('ready');
    if (decoded.status !== 'ready' || decoded.frame.type !== 'event' || decoded.frame.event.ev !== 'system.upgrade_status') throw new Error('Expected upgrade event');
    store.acceptStatus(decoded.frame.event.data);
    store.acceptInfo(info, revision);
    expect(store.read().info?.draining).toBe(true);
    expect(store.read().activeDispatches).toBe(2);
    store.dismissReceipt();
    store.acceptStatus({ draining: false, activeDispatches: 0, receipt });
    expect(store.read().dismissedReceipt).toBe(receipt.jobId);
    expect(store.read().receipt).toEqual(receipt);
    store.acceptStatus({ draining: false, activeDispatches: 0, receipt: { ...receipt, jobId: 'older', finishedAt: 1 } });
    expect(store.read().receipt?.jobId).toBe('job-1');
  });
  it('rejects malformed upgrade counts', () => {
    expect(decodeEventFrame({ kind: 'system.upgrade_status', payload: { draining: true, activeDispatches: -1 } })).toMatchObject({ status: 'ready', frame: { type: 'malformed-event' } });
  });
});
