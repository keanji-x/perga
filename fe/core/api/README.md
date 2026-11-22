# core/api

Platform-neutral HTTP contracts and decoding. Import specific owning modules;
there is no barrel. `types.ts` defines request/transport ports, `client.ts`
executes one admitted request, and `perga-v1.ts` declares the current Perga DTOs.
Domain operation factories construct `/api/v1` and `/api/today` requests.

The browser adapter implements `ApiTransportPort`. It must preserve credentials,
headers, body, cancellation and timeout semantics. Core does not implement
fetch, retries, navigation or React Query cache policy. Failures are data:
unauthorized (401), transport, HTTP and response decode errors remain distinct.
Only an injected unauthorized channel receives 401 notifications; other failures
must not silently log out the user. Empty 204 responses decode to undefined.

`generated/openapi.json` is a manually maintained snapshot checked against Host
routes by `tools/architecture/openapi-contract.test.ts`. `generated/wire.ts`
contains the remaining wire types. The generated directory names do not imply
an active generator: `npm run gen:api` only prints a notice. Update producers,
consumers, schemas, fixtures and the snapshot together. Keep the current
contracts aligned with Host `/api/v1` and `/api/today`; do not restore Rust
calm-server routes or a historical compatibility layer.

These paths are frozen ownership surfaces. Authorized changes require exact
OWNERSHIP-CHANGE trailers and applicable contract verification. Error semantics
and transport lifetime stay in their owning modules.
