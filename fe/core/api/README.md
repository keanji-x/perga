# core/api

Platform-neutral HTTP contracts and decoding. Import specific owning modules;
there is no barrel. `types.ts` defines request/transport ports, `client.ts`
executes one admitted request, and `perga-v1.ts` declares the current Perga DTOs.
Domain operation factories construct `/api/v1` and `/api/v1/today` requests.

The browser adapter implements `ApiTransportPort`. It must preserve credentials,
headers, body, cancellation and timeout semantics. Core does not implement
fetch, retries, navigation or React Query cache policy. Failures are data:
unauthorized (401), transport, HTTP and response decode errors remain distinct.
Only an injected unauthorized channel receives 401 notifications; other failures
must not silently log out the user. Empty 204 responses decode to undefined.

`npm run gen:api` exports the real Host endpoint table and serializer metadata to
`generated/openapi.json` and generates `generated/wire.ts` from that document.
`npm run gen:api -- --check` fails on drift in either artifact. The Host drift
integration test independently compares the committed document to a fresh export.
`perga-v1.ts` owns runtime decoding and neutral defaults; test its payloads against
the generated contract. Frontend presentation types live in
`core/domain/presentation-types.ts` and are not HTTP wire contracts.
Keep producers, consumers, fixtures and runtime decoders aligned when regenerating.

These paths are frozen ownership surfaces. Authorized changes require exact
OWNERSHIP-CHANGE trailers and applicable contract verification. Error semantics
and transport lifetime stay in their owning modules.
