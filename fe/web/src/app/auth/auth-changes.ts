import { AUTH_CHANGE_KEY } from '../../../../core/keys/storage.ts';

export interface AuthChanges {
  publish(): void;
  subscribe(listener: () => void): () => void;
}

function openChannel(browser: Window & typeof globalThis): BroadcastChannel | undefined {
  try { return new browser.BroadcastChannel(AUTH_CHANGE_KEY); }
  catch { return undefined; }
}

/** Receivers get their own verdict from the Host after an empty notification. */
export function createAuthChanges(browser: Window & typeof globalThis, storage: Pick<Storage, 'setItem' | 'removeItem'>): AuthChanges {
  let channel: BroadcastChannel | undefined;
  let subscribers = 0;
  return {
    publish() {
      const sender = channel ?? openChannel(browser);
      if (sender !== undefined) {
        try { sender.postMessage(null); return; }
        catch { /* Fall back to a storage event. */ }
        finally { if (sender !== channel) sender.close(); }
      }
      try {
        storage.setItem(AUTH_CHANGE_KEY, '1');
        storage.removeItem(AUTH_CHANGE_KEY);
      } catch { /* Session validation still works when storage is unavailable. */ }
    },
    subscribe(listener) {
      if (subscribers++ === 0) channel = openChannel(browser);
      const onStorage = (event: StorageEvent) => {
        if (event.key === AUTH_CHANGE_KEY && event.newValue === '1') listener();
      };
      channel?.addEventListener('message', listener);
      browser.addEventListener('storage', onStorage);
      return () => {
        channel?.removeEventListener('message', listener);
        if (--subscribers === 0) {
          channel?.close();
          channel = undefined;
        }
        browser.removeEventListener('storage', onStorage);
      };
    },
  };
}
