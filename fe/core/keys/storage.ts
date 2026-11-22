import type {
  Codec,
  StorageReadResult,
  StorageRemoveResult,
  StorageWriteResult,
} from '../state/types.js';

declare const storageKeyBrand: unique symbol;

export type StorageKey = string & { readonly [storageKeyBrand]: true };

/** Perga browser database; legacy databases are intentionally not imported. */
export const IDB_DB_NAME = 'perga';
export const SYNC_CURSOR_KEY = 'perga:sync:cursor' as StorageKey;
export const DB_INSTANCE_ID_KEY = 'perga:db_instance_id' as StorageKey;
/** The kernel database's stable identity — survives a process restart. Read receipts are scoped by it; `DB_INSTANCE_ID_KEY` is the per-boot id and must never seed them. */
export const DATABASE_ID_KEY = 'perga:database_id' as StorageKey;
export const THEME_KEY = 'perga:theme' as StorageKey;
export const AUTH_CHANGE_KEY = 'perga:auth-change' as StorageKey;
export const TRACK_CREATE_DRAFTS_KEY = 'perga:track-create-v1' as StorageKey;
export const PENDING_TRACK_INITIAL_SENDS_KEY = 'perga:pending-track-initial-sends-v1' as StorageKey;

/** Creates application-owned keys; segments must already be normalized and non-empty. */
export function createStorageKey(...segments: readonly [string, ...string[]]): StorageKey {
  if (segments.some((segment) => segment.length === 0 || segment.includes(':'))) {
    throw new TypeError('Storage key segments must be non-empty and may not contain colons');
  }
  return `perga:${segments.join(':')}` as StorageKey;
}

/** Key-branded storage adapter consumed by session, event, and application assembly. */
export interface StorageAdapterPort {
  read<T>(key: StorageKey, codec: Codec<T>): Promise<StorageReadResult<T>>;
  write<T>(key: StorageKey, value: T, codec: Codec<T>): Promise<StorageWriteResult>;
  remove(key: StorageKey): Promise<StorageRemoveResult>;
}
