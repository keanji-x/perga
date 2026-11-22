# C1: Host contract authority

Rebased baseline: `1977f91a` (PR #135 auth, plus PR #139 and PR #137).
Compared with original reviewed C1 `f9489282` and the new auth baseline.

## HTTP authority and generation

Serving and offline export share authenticated service registration through
`PergaHttpEndpoints.CreateBuilder` and routes through `MapPublishedHttpEndpoints`, which registers the
real `/api/v1/**` and `/healthz` Minimal API table. `--export-openapi <path>` builds
that table and disposes the application without starting a listener, hosted
services, or the Kernel. No external packages are added.

The exporter reads RouteEndpoint handler signatures, response metadata and Host
serializer metadata. Nullable values, serialized enum values, path/query/header
parameters and request/response properties come from their owning CLR types.
Opaque JsonElement payloads remain unconstrained. Stable ordering and no runtime
version/timestamp inputs make the OpenAPI 3.1.0 output deterministic.

Manually parsed request bodies use `JsonRequestContract`, which describes their
type and optionality without participating in ASP.NET content-type matching.
`IAcceptsMetadata` would change existing bodyless-request errors into unmatched
routes. Runtime result types supply success response types; explicit metadata
covers dynamic status codes, replay, binary responses and nullable daily results.
Missing request/response contracts and ambiguous schema names fail export.
The shared Host exception envelope describes default error responses.

`npm run gen:api` runs the exporter and generates both `openapi.json` and `wire.ts`.
After restoring the existing .NET solution once, `npm run gen:api -- --check`
re-exports and fails on drift in either artifact. Host integration tests compare a
fresh export to the committed document. Frontend integrity tests resolve every
schema reference and regenerate the complete committed wire module in the normal
unit gate, with separate field-type, nullability and enum-drift negatives.
A separate parity check compares runtime DTO decoder fields with exported fields. Runtime decoders retain
presentation defaults; admission rules, such as accepted notification values,
remain Kernel policy rather than inferred JSON structure.

## Auth integration

PR #135 authentication, raw/forwarded transport validation, origin/write-source
rules, owner/management policies, authenticated identities, protected API/MCP
fallbacks and WebSocket lifecycle remain authoritative. Normal serving middleware
order is unchanged. Export returns before transport startup and never binds.

Auth handlers retain their exact manual JSON parsing, body limits, KDF/rate-limit,
header checks, cookie behavior and status selection. Documentation-only metadata
publishes login/session identity responses, logout's optional cookie and guarded
session header, login errors and response headers. Export derives effective
security from registered authorization policies and fails on unknown schemes;
anonymous health/login and optional-cookie logout override the global cookie
contract. No runtime `Accepts` or replacement auth/body-validation layer is added.

## Today, DTOs and provider identity

Today moves directly to `/api/v1/today` with camelCase JSON and query names.
Explicit snake_case DTO property attributes are removed too. An absent dated
daily track remains a JSON null response. `/api/v1` origin protection covers the
routes; no compatibility route or legacy guard prefix is retained.

Area and Track HTTP/event caches consume Host DTOs directly. Workspace and
activity projections stay in the domain. Old snake_case carrier alternatives,
Track projection round-trips and duplicate generated presentation models are
removed. Frontend-only presentation types now live under `core/domain`.
The parity check also exposed missing Track flow fields and unsupported gate
`cwd` and VCS `oldPath` decoder fields, plus obsolete task/directory/template
aliases; these are aligned with the actual Host.

Provider menus, catalogs, creation and session reads retain opaque runtime IDs,
including unfamiliar registrations. Query values are URL-encoded. Product label
lookup is presentation-only and never constructs a wire identifier. Existing
canonical IDs remain valid when explicitly advertised by the runtime.

No migrations, external dependencies, Kernel/Storage edits, or DESIGN.md changes.

## Verification

Final rebased production tree verified on 2026-11-22 against `1977f91a`:

- Full .NET solution: **8 projects, 1,100 passed, 0 failed, 0 skipped**, including
  all new PR #135 auth, rate-limit, credential/session and WebSocket tests.
  Ran `/home/kenji/.dotnet/dotnet test Perga.sln --no-restore -m:1` with normal
  console logging and a 60-second hang diagnostic. `DOTNET_USE_POLLING_FILE_WATCHER=1`
  was set; ignored test-output OAR/terminal bridges were used as before.
- `npm run gen:api -- --check`: exit 0; **49 paths, 57 operations, 116 schemas**.
  The 113 original C1 schemas are structurally unchanged; auth adds LoginRequest,
  SessionIdentityDto and SystemAuthStatusDto. Full OpenAPI/wire parity is enforced.
- `npm run lint`: exit 0 (JS/ownership, CSS/theme and dependency architecture).
- `npm test`: **354 files, 4,658 passed, 0 failed, 0 skipped**.
- `npm run build`: exit 0; existing Vite chunk-size advisory remains.
- `npm run test:browser`: **210 files, 1,969 passed, 0 failed, 0 skipped**, including
  both Chromium/coarse-pointer projects and the preserved PR #135 frontend flows.
- Focused auth/Today/origin/export checks: **114 Host tests and 43 frontend
  contract tests passed**.
- Targeted auth mutation: removing Today's UserSession requirement makes the
  independent route-metadata assertion fail. The source was restored byte-for-byte
  and that assertion passed again. No serving listener is started by this check.

Two independent read-only L2 rebase delta reviews compared both `f9489282` and
`1977f91a`; both are accepted with zero blockers. They independently verified the
upstream auth handlers, identities, middleware, policies, MCP/WS boundaries and
frontend auth flow, as well as all retained C1 DTO/provider/Today invariants.

The dual-baseline range-diff records behavioral integration rather than a
mechanical rebase. Critical upstream auth/transport/WS/MCP implementations and
all normal serving middleware/fallback ordering remain unchanged. Scope and
whitespace audits pass; no Kernel/Storage/DESIGN.md/tools source, dependency or
lockfile changes are introduced. All 16 required ownership trailers are retained;
full lint is repeated after the final commit amendment to validate their format.
