# Configurable Cleartext HTTP for Trusted Local/LAN Deployments (L2)

## Outcome and Scope

Allow operators running Perga on a trusted local or private network (LAN, VPN, Docker/WSL bridge, or HTTP-only intranet reverse proxy) to explicitly opt out of the HTTPS requirement on `/api/*`, while keeping the default fail-closed HTTPS behavior and all authentication, session, CSRF/Origin, Host-pinning, and system-management boundaries intact.

## Configuration Contract (`PergaHostOptions`)

- New boolean option: `Perga:AuthAllowInsecureHttp` (default `false`), with environment variable alias `PERGA_AUTH_ALLOW_INSECURE_HTTP` (explicit `Perga:AuthAllowInsecureHttp` takes precedence over the environment alias).
- Fail-fast startup validation in `ValidateAuth()`:
  - `AuthAllowInsecureHttp == true` combined with `AuthCookieSecurePolicy == AuthCookieSecurePolicy.Always` throws `InvalidOperationException`, because browsers reject `Secure` / `__Host-` cookies on cleartext non-loopback HTTP origins.
  - When `AuthAllowInsecureHttp == true`, `AuthPublicOrigin` accepts valid `http://` or `https://` origins (still validated via `RequestOriginGuard.TryOrigin`). When `false` (default), `AuthPublicOrigin` continues to require `https://`.
- When `AuthAllowInsecureHttp == true`, `Program.cs` emits a startup warning log noting that cleartext HTTP is enabled on `/api`.

## Transport, Cookie, and Guard Semantics

1. **`TransportSecurityMiddleware`**:
   - Default (`AuthAllowInsecureHttp == false`): unchanged. Direct loopback HTTP is permitted only in `Direct` + `Auto` mode; all other cleartext `/api/*` requests return `403 insecure_transport_required` (except verified direct loopback management requests to `/api/v1/system/*`).
   - Opt-in (`AuthAllowInsecureHttp == true`): cleartext HTTP requests on `/api/*` are allowed in both `Direct` and `Proxied` ingress modes.
   - In `Proxied` mode, direct loopback system-management verification (`VerifiedLoopbackManagement`) still executes so local `perga-ctl` calls to `/api/v1/system/*` continue to bypass `Proxied` public-Host pinning in `RequestOriginGuard`.
2. **`WebSessionCookie`**:
   - Unchanged. With `AuthCookieSecurePolicy == Auto`, HTTP requests automatically receive `perga_session` (`HttpOnly; SameSite=Lax; Path=/`, without `Secure`), while HTTPS requests continue to receive `__Host-perga_session` with `Secure`.
3. **Unchanged Security Boundaries**:
   - Owner password authentication (`PergaAuthPolicies.UserSession`) remains required on all protected `/api/*` and WebSocket endpoints.
   - `RequestOriginGuard` continues to enforce Host pinning in `Proxied` mode, loopback DNS-rebinding protection on loopback sockets, `Origin` / `Sec-Fetch-Site` checks, WebSocket `Origin` checks, and browser write CSRF guards.
   - `SystemManagementAuthorization` continues to require HTTPS for any non-loopback or forwarded system-management Bearer request.
4. **Operational Cautions**:
   - In `Proxied` mode with `AuthAllowInsecureHttp == true`, keep the backend Host listener bound to loopback or firewalled to the reverse proxy so direct peers cannot bypass the proxy.
   - When pinning an `https://` `AuthPublicOrigin`, do not add that same hostname to `AllowedHosts` alongside `AuthAllowInsecureHttp == true` unless you intentionally accept cleartext HTTP cookies for that host.

## Verification Plan (L2)

- Unit/integration tests in `tests/Perga.Host.IntegrationTests/AuthHttpIntegrationTests.cs`:
  - Startup validation: reject `AuthAllowInsecureHttp=true` + `AuthCookieSecurePolicy=Always`; accept `http://` `AuthPublicOrigin` only when `AuthAllowInsecureHttp=true`; reject malformed `AuthPublicOrigin`.
  - Configuration binding: `PERGA_AUTH_ALLOW_INSECURE_HTTP` alias and explicit `--Perga:AuthAllowInsecureHttp` precedence.
  - Direct mode with `AuthAllowInsecureHttp=true`: non-loopback HTTP login/session/API/logout cookie flow, HTTPS cookie upgrade, preserved `401` auth enforcement, preserved `RequestOriginGuard` Origin/CSRF/DNS-rebinding rejections, and preserved `SystemManagementAuthorization` rejection on non-loopback HTTP.
  - Real Kestrel socket Proxied mode with `AuthAllowInsecureHttp=true` and `http://` `AuthPublicOrigin`: proxied HTTP login/API works, foreign/unpinned Host and cross-site requests fail closed, direct loopback `perga-ctl` management still works, and forwarded HTTP management is still rejected.
  - Default mode regression: existing strict HTTPS tests remain green without modification.
- Full `.NET` suite (`dotnet test Perga.sln`) and two independent read-only L2 code reviews.
