# Perga Web Login & Session Authentication Design

## Outcome & Scope

Replace Perga's unauthenticated single-owner web mode (`AuthRequired: false` and `GET /api/v1/bootstrap?scope=whoami`) with fail-closed username/password authentication and server-side stateful sessions stored in SQLite, while keeping Perga's Dapper/SQLite single-writer architecture clean and reusing mature ASP.NET Core / .NET 11 primitives wherever they fit.

### Design Principles

1. **Prefer standard .NET 11 / ASP.NET Core primitives and official lightweight packages over hand-rolled security plumbing**:
   - Password hashing: `Microsoft.Extensions.Identity.Core` (`PasswordHasher<AuthUserRecord>`, PBKDF2-HMAC-SHA512, built-in format versioning & `SuccessRehashNeeded` automatic upgrade). No EF Core or Identity tables.
   - HTTP AuthN/AuthZ pipeline: ASP.NET Core `AddAuthentication()` + `AddAuthorizationBuilder()` + typed `AuthenticationHandler` schemes + declarative `.RequireAuthorization(policy)` + custom `IAuthorizationMiddlewareResultHandler` for uniform JSON `ApiErrorResponse`.
   - Login rate limiting & KDF throttling: `System.Threading.RateLimiting` (`ConcurrencyLimiter` + `SlidingWindowRateLimiter`).
   - CLI parsing: `System.CommandLine` (`Perga.Host auth <subcommand>`).
   - Forwarded headers: ASP.NET Core `UseForwardedHeaders(...)` with explicit `KnownProxies`.
   - Origin/host guard: extend the existing `RequestOriginGuard` (one authoritative guard).
2. **Keep custom code strictly where Perga's architecture requires it**:
   - Two minimal SQLite tables (`auth_users`, `auth_sessions`) integrated into `SqliteTrackStore` (`0026_auth_users_sessions.sql`).
   - Unified WebSocket session lifecycle (`AuthenticatedWebSocket`, `AuthWebSocketRegistry`, `WsSessionCloseCoordinator`) for live `1008` revocation disconnect.
   - Focused CLI deployment hook in `tools/perga-deploy/perga_ctl.py` (no custom Unix-socket HTTP transport rewrite).
3. **Preserve operational debuggability and simplicity**:
   - Scrub internal exception details from HTTP responses, **never** strip full `Exception` + stack traces from server-side 500 error logs.
   - `auth status` reads SQLite directly with `Mode=ReadOnly` (`PRAGMA query_only = ON`) — never copy the database file to `/tmp`.
   - Strongly-typed enum options (`AuthIngressMode`, `AuthCookieSecurePolicy`) with consistent fail-fast startup validation.
   - Logout revokes server state, disconnects live WebSockets, and clears the browser cookie (guarded by `X-Perga-Session-Id` against stale-tab races).

---

## 1. Storage Schema & Concurrency (`Perga.Storage.Sqlite`, `Perga.Kernel`)

### Migration `0026_auth_users_sessions.sql`
Migrations `0001`–`0025` remain byte-frozen. Add forward migration `0026_auth_users_sessions.sql`:

- `auth_users`:
  - `id TEXT PRIMARY KEY NOT NULL` (`usr_<32-hex>`)
  - `username TEXT NOT NULL UNIQUE COLLATE NOCASE`
  - `display_name TEXT NOT NULL`
  - `role TEXT NOT NULL DEFAULT 'owner' CHECK (role IN ('owner', 'member'))`
  - `password_hash TEXT NOT NULL`
  - `credential_version INTEGER NOT NULL DEFAULT 1 CHECK (credential_version >= 1)`
  - `session_generation INTEGER NOT NULL DEFAULT 1 CHECK (session_generation >= 1)`
  - `password_updated_at_ms INTEGER NOT NULL`
  - `disabled_at_ms INTEGER`
  - `created_at_ms INTEGER NOT NULL`
  - `updated_at_ms INTEGER NOT NULL`
  - Partial unique index `idx_auth_users_single_owner ON auth_users(role) WHERE role = 'owner'` enforcing single owner in v1.
- `auth_sessions`:
  - `id TEXT PRIMARY KEY NOT NULL` (`aus_<32-hex>`)
  - `user_id TEXT NOT NULL REFERENCES auth_users(id) ON DELETE CASCADE`
  - `token_hash TEXT NOT NULL UNIQUE` (lowercase 64-char SHA-256 hex of raw 32-byte token)
  - `created_at_ms INTEGER NOT NULL`
  - `last_seen_at_ms INTEGER NOT NULL`
  - `expires_at_ms INTEGER NOT NULL`
  - `revoked_at_ms INTEGER`
  - `user_agent TEXT`
  - `created_ip TEXT`
  - Indexes: active sessions by `user_id` (`WHERE revoked_at_ms IS NULL`), `expires_at_ms`, and `revoked_at_ms` (`WHERE revoked_at_ms IS NOT NULL`).

### Kernel Port (`IAuthStore` / `IAuthOwnerWriter`)
- Owner mutations (`CreateInitialOwnerAsync`, `RotateOwnerPasswordAsync`, `RenameOwnerAsync`, `SetOwnerDisabledAsync`, `RevokeAllSessionsAsync`) atomically increment `credential_version` / `session_generation` and revoke active sessions where applicable.
- `TryCreateSessionIfVersionMatchesAsync(...)`: runs inside SQLite write transaction (`_writer`) with CAS condition `WHERE id = @UserId AND role = 'owner' AND disabled_at_ms IS NULL AND credential_version = @ExpectedCredentialVersion AND session_generation = @ExpectedSessionGeneration`. Password verification runs outside the transaction; if the owner rotated/disabled/revoked mid-login, session insertion returns `null` and login is rejected.
- `UpgradePasswordHashIfVersionMatchesAsync(...)`: optional best-effort rehash update when `PasswordHasher` returns `PasswordVerificationResult.SuccessRehashNeeded`, conditional on matching `credential_version` (does NOT bump `session_generation`).
- `GetValidOwnerSessionByTokenHashAsync`, `IsOwnerSessionValidAsync`, `TouchSessionLastSeenIfStaleAsync`, `RevokeSessionByTokenHashAsync`.
- `MaintenanceRepository.cs` (delegated via `SqliteTrackStore`): batched pruning (100 rows/batch) of sessions expired or revoked more than 7 days ago.
- `AuthRepository.cs` (delegated via `SqliteTrackStore`): implements `IAuthStore` over `SqliteStoreContext`.
- `SqliteMigrator.cs`: expose `CurrentSchemaVersion = 26`, `SqliteFutureSchemaException`, `EnsureMigratedAsync(dbPath, allowMigrate, ct)` serialized by an exclusive `{dbPath}.migrate.lock` file lock when applying migrations so concurrent startup/CLI migrations cannot race.
- `SqliteAuthAdministration.cs`: standalone CLI access using `SqliteOpenMode.ReadOnly` + `PRAGMA query_only = ON; PRAGMA busy_timeout = 2000;` for reads (zero DB copying) and `SqliteOpenMode.ReadWrite` + `BEGIN IMMEDIATE` + `PRAGMA busy_timeout = 30000; PRAGMA foreign_keys = ON;` for owner writes.

---

## 2. Host Auth Architecture (`Perga.Host`)

### NuGet Dependencies (`Perga.Host.csproj`)
- `Microsoft.Extensions.Identity.Core` `11.0.0` (for `PasswordHasher<AuthUserRecord>`)
- `System.CommandLine` `3.0.0` (for `Perga.Host auth <subcommand>`)

### Strongly-Typed Configuration (`PergaHostOptions`)
- `AuthIngressMode AuthIngressMode { get; set; } = AuthIngressMode.Direct;` (`Direct | Proxied`, env `PERGA_AUTH_INGRESS_MODE`)
- `AuthCookieSecurePolicy AuthCookieSecurePolicy { get; set; } = AuthCookieSecurePolicy.Auto;` (`Auto | Always`, env `PERGA_AUTH_COOKIE_SECURE`)
- `bool AuthAllowInsecureHttp { get; set; } = false;` (opt-in cleartext HTTP for trusted local/LAN/VPN deployments; incompatible with `AuthCookieSecurePolicy.Always`; env `PERGA_AUTH_ALLOW_INSECURE_HTTP`)
- `int AuthSessionLifetimeDays { get; set; } = 30;` (`1..365`, throws on invalid, env `PERGA_AUTH_SESSION_DAYS`)
- `string? AuthPublicOrigin { get; set; }` (required in `Proxied` mode unless `AllowedHosts` is non-empty; requires `https://` unless `AuthAllowInsecureHttp` is true; env `PERGA_AUTH_PUBLIC_ORIGIN`)
- `bool TrustForwardedHeaders { get; set; }` (env `PERGA_TRUST_FORWARDED_HEADERS`; requires `Proxied` mode)
- `string[] TrustedProxyAddresses { get; set; } = [];` (IP literals; defaults to `127.0.0.1` and `::1` when `TrustForwardedHeaders` is true)
- `TimeSpan AuthWsRevalidationInterval { get; set; } = TimeSpan.FromSeconds(15);` (`1s..5m`, throws on invalid)
- Reuse existing `AllowedHosts` and `AllowedOrigins` on `PergaHostOptions`.

### Password Hashing (`AuthPasswordHasher`)
- Wraps `PasswordHasher<AuthUserRecord>` (`IdentityV3`, `IterationCount = 220_000` in production, injectable options for fast tests).
- Exposes `Hash(string password)`, `Verify(string password, string encodedHash) -> PasswordVerificationResult`, and `VerifyDummy(string password)` (against a startup-generated `DummyHash`).
- Automatically verifies legacy/higher iteration counts embedded in IdentityV3 hashes and signals `SuccessRehashNeeded` on iteration upgrades.

### Request Pipeline Order (`Program.cs`)
1. `CaptureRawTransportMiddleware` (records pre-forwarding `RemoteIpAddress`, `IsHttps`, and whether `Forwarded` / `X-Forwarded-*` headers were present).
2. If `TrustForwardedHeaders`: standard `app.UseForwardedHeaders(...)` configured with `ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto`, `ForwardLimit = 1`, cleared default `KnownNetworks` / `KnownIPNetworks` / `KnownProxies`, and explicit `TrustedProxyAddresses` (or loopback defaults).
3. `HttpErrorHandling.InvokeExceptionShieldMiddlewareAsync` (scrubs client response bodies, logs full `Exception ex` + method + path on 500s).
4. `TransportSecurityMiddleware` enforces HTTPS on `/api/*` by default. Direct loopback HTTP is available in `Direct` ingress with `Auto` cookie security, and operators on trusted private networks can explicitly opt in to cleartext HTTP via `AuthAllowInsecureHttp = true` (requires `Auto` cookie security). In strict transport modes (and in `Proxied` mode generally), direct loopback HTTP remains available on `/api/v1/system/*` with a verified management bearer token, a loopback Host, and no forwarding or browser-origin headers. Rejected requests receive `403 insecure_transport_required` with connection guidance.
5. `RequestOriginGuard` checks loopback Hosts, pins the public Host in `Proxied` mode, and recognizes the same verified loopback management requests. It checks `Origin` and `Sec-Fetch-Site`, requires `Origin` on WebSockets, and requires JSON or an acceptable `Origin`/`Referer` on browser write APIs.
6. `app.UseWebSockets(...)`
7. `app.UseRouting()`
8. `app.UseAuthentication()` + `app.UseAuthorization()`
9. Fail-closed guard for unmatched `/api/*` and `/mcp/*` routes (unauthenticated requests to unknown `/api/*` or `/mcp/*` get `401`, authenticated unknown routes get JSON `404`; never falls through to SPA `index.html`).
10. Static files / endpoints / SPA fallback (for non-API, non-MCP GET/HEAD paths).

### Authentication Schemes & Authorization Policies
- **Schemes**:
  - `PergaAuthSchemes.WebSession`: validates `__Host-perga_session` (HTTPS) or `perga_session` (cleartext HTTP) cookie via `IAuthStore.GetValidOwnerSessionByTokenHashAsync`.
  - `PergaAuthSchemes.SystemManagement`: validates `Bearer <token>` via `SystemManagementAuthorization.Verify`.
  - `PergaAuthSchemes.McpSession`: validates `Bearer <mcp-session-token>` via `McpStreamableHttpEndpoints.AuthenticateCaller`.
- **Policies**:
  - `PergaAuthPolicies.UserSession`: `WebSession` + role `owner` (`/api/v1/*`, `/api/today/*`, `/api/v1/auth/session`, WebSockets).
  - `PergaAuthPolicies.UserOrManagement`: `WebSession` OR `SystemManagement` (`GET /api/v1/system/info`).
  - `PergaAuthPolicies.SystemManagementOnly`: `SystemManagement` only (`GET /api/v1/system/auth-status`, `POST /api/v1/system/drain`, `POST /api/v1/system/reload`).
  - `PergaAuthPolicies.McpSessionOnly`: `McpSession` only (`/mcp/*`).
  - `GET /healthz`, `POST /api/v1/auth/login`, `POST /api/v1/auth/logout`: `.AllowAnonymous()`.
- **`PergaAuthorizationResultHandler` (`IAuthorizationMiddlewareResultHandler`)**: emits uniform JSON `ApiErrorResponse` (`401 unauthorized`, or `403 system_management_forbidden` on `SystemManagementOnly`).

### Login Rate Limiting (`LoginRateLimiter`)
- Built on `System.Threading.RateLimiting`:
  - Global KDF gate: `ConcurrencyLimiter(PermitLimit = 4, QueueLimit = 32)` with 2s wait timeout (`429 login_throttled`, `Retry-After: 2`).
  - Failure windows: partitioned `SlidingWindowRateLimiter` — per IP (`20 / 15min`) and per `(ip, normalizedUsername)` (`5 / 15min`), configurable via `LoginRateLimitOptions` for fast deterministic tests.
  - Check failure windows before joining the KDF queue. In-flight reservations prevent concurrent requests from exceeding either window. Exhausted windows use retry metadata when available. The partition tables have independent limits and reclaim entries after their failure permits replenish and their in-flight requests finish.
  - Only **non-successful** KDF completions (wrong password, disabled user, unknown user, OR client disconnect after entering KDF) consume failure permits; successful logins do not burn failure budget.

### HTTP Auth Endpoints (`AuthEndpoints`)
- `POST /api/v1/auth/login`:
  - Enforces `application/json` (`415`) and `<= 4096` byte body (`413`), strict JSON deserialization (`UnmappedMemberHandling.Disallow`), zeroes raw body buffer.
  - Trims and lowercases usernames before checking the format (`1..64` ASCII `[a-z0-9._@-]` starting with alphanumeric). Passwords require `1..1024` Unicode scalar values, `<= 4096` UTF-8 bytes, and no NUL. Creation and rotation require at least 12 scalar values. Missing or blank credentials return `400 invalid_argument`. Malformed nonblank credentials return `401 invalid_credentials`.
  - Acquires rate-limit & KDF slot, runs `AuthPasswordHasher.Verify` (or `VerifyDummy`) off the request thread.
  - If 0 users exist after dummy verification, returns `403 auth_not_configured` with CLI next step.
  - On valid password: if `SuccessRehashNeeded`, best-effort updates hash; creates session atomically via CAS; sets cookie (`HttpOnly`, `SameSite=Lax`, `Path=/`, `Secure` on HTTPS or `Always`) + `X-Perga-Session-Id` header; returns `200 SessionIdentityDto`.
- `GET /api/v1/auth/session`: `Cache-Control: no-store`, `Vary: Cookie`, best-effort `TouchSessionLastSeenIfStaleAsync` (15m interval), returns `200 SessionIdentityDto`.
- `POST /api/v1/auth/logout`: `Cache-Control: no-store`. Reads cookie; if active session exists and optional `X-Perga-Session-Id` header matches (or is absent), revokes session in SQLite, calls `AuthWebSocketRegistry.DisconnectSession`, clears cookie (`Response.Cookies.Delete` with matching options), returns `204`. If `X-Perga-Session-Id` does not match, returns `204` without revoking or clearing cookie.

### WebSocket Session Lifecycle (`AuthenticatedWebSocket`, `AuthWebSocketRegistry`, `WsSessionCloseCoordinator`)
- Register lease before accepting upgrade (and before dialing terminal bridge upstream in `TerminalWebSocketRelayHandler`), verify session validity before activating forwarding.
- Single-flight periodic DB revalidation (`AuthWsRevalidationInterval`, 2s deadline, 2 consecutive errors fail-closed).
- Immediate `DisconnectSession(sessionId)` on logout -> serialized `CloseOutputAsync(1008, "session_revoked")` with abort fallback.
- Both `EventsWebSocketHandler` and `TerminalWebSocketRelayHandler` run through `AuthenticatedWebSocket.RunAsync`.

### CLI (`AuthCliCommand` using `System.CommandLine`)
- Entry: `Perga.Host auth <subcommand>` (`init-owner`, `passwd`, `rename-owner`, `disable-owner`, `enable-owner`, `revoke-sessions`, `status`).
- Shared options: `--db-path`, `--data-dir`, `--allow-migrate` (for mutating commands).
- Password input: interactive masked prompt with confirmation, or `--password-stdin` (1 line from stdin). Passwords are never accepted as CLI arguments.
- `status` prints JSON `SystemAuthStatusDto` by reading SQLite directly in `ReadOnly` mode (zero file copying).
- `tools/perga-deploy/perga_ctl.py`: add `perga-ctl auth <subcommand>` holding shared `upgrade.lock` after `idle()` and invoking `current/host/Perga.Host auth ... --db-path <db>` directly in a single subprocess call.

---

## 3. Frontend & Contracts (`fe/`)

- `fe/core/api/auth.ts`:
  - Remove `bootstrap?scope=whoami` stub.
  - `whoamiOperation()` -> `GET /api/v1/auth/session`
  - `loginOperation(body)` -> `POST /api/v1/auth/login` (`expectedStatuses: [200]`, checks `x-perga-session-id` header matches `sessionId`)
  - `logoutOperation(sessionId?)` -> `POST /api/v1/auth/logout` (`expectedStatuses: [204]`, optional `X-Perga-Session-Id`)
- `fe/core/api/client.ts` & `fe/web/src/app/providers/transport.ts`: forward response headers and parse numeric `Retry-After` into `ApiFailure.retryAfterSeconds`.
- `fe/web/src/app/auth/`:
  - `auth-changes.ts`: zero-payload cross-tab sync (`BroadcastChannel` + `storage` fallback).
  - `logout-gate.tsx` + `logout-gate.module.css`: wraps `SessionGate`, prevents premature reload on logout failure, preserves `sessionId` for retry.
  - `session-gate.tsx`: keys `<CurrentUserScope key={state.value.sessionId}>`, subscribes to `authChanges`, surfaces `insecure_transport_required` error.
  - `login.ts` & `fe/web/src/features/auth/login-page/public.tsx`: required-field validation, `429 Retry-After` countdown message, `auth_not_configured` guidance.
  - `production-app.tsx`, `router/public.tsx`, `sidebar.tsx`: display authenticated `displayName` and wire `Sign out`.
- `fe/web/src/systems/events/websocket-driver.ts`: trigger session re-probe on close codes `1008` and `1006`.
- `fe/core/domain/conversation-display.ts`: treat historical `local-owner` human messages as `'you'` when `viewerIsOwner` is true.
- `fe/core/api/generated/openapi.json` + `fe/tools/architecture/openapi-contract.test.ts`:
  - Use document-level default `security: [{ pergaSessionCookie: [] }]`, define `pergaSessionCookie` & `systemManagementBearer`, document `/api/v1/auth/*` and `/api/v1/system/auth-status`, and override `security` only on public (`[]`) or management routes.
- Playwright E2E (`fe/e2e/auth.spec.ts` + `fe/e2e/helpers/auth-host.ts`): live `Perga.Host` + SQLite + Chromium verifying login, cookie flags, reload persistence, cross-tab sync, failed/retried logout + cookie removal, `1008` WS disconnect on revocation, `429 Retry-After`, and trusted TLS reverse proxy.

---

## 4. Verification Plan (L2)

- Backend: `dotnet test Perga.sln` (including dedicated Storage, Kernel, Auth CLI, Auth HTTP/Cookie/Schemes, RateLimit, WebSocket lifecycle, Kestrel TLS/proxy integration tests, and all existing integration suites).
- Deploy: `python3 -m unittest discover -s tools/perga-deploy/tests`.
- Frontend: `(cd fe && npm run lint && npm run build && npm test && npm run test:browser && npx playwright test e2e/auth.spec.ts)`.
- Two independent L2 code reviews (`read_only` workers) covering authority, persistence, concurrency, isolation, contracts, and tests.
