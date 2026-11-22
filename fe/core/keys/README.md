# core/keys

This frozen module owns persistent key constants, the dynamic key factory and
branded storage adapter ports. Logout, cache reset and recovery consumers must
use the same declared keys instead of spelling namespace literals themselves.

Browser state uses the `perga` IndexedDB database. Keys use `perga:` followed by
colon-separated segments, including `perga:theme`. Empty segments and segments
containing colons are rejected synchronously. Adapters accept `StorageKey` and
return explicit core/state results so platform and quota errors remain data.

P3 intentionally changed the namespace and database identity. Existing Neige
browser data is not read, copied or migrated. New Perga keys remain stable after
this reset. The architecture gate enforces central ownership of key literals;
`perga://` report URLs are a separate domain contract.

This module does not access localStorage or IndexedDB, select codecs, or perform
migration/cleanup. Those platform and lifecycle responsibilities are injected by
the browser layer. Do not add arbitrary raw-key casts at consumer call sites.
