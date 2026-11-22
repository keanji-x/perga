# Host request origin boundary (L2)

## Outcome and threat model

A hostile web page must not read or mutate the local Host through HTTP,
WebSocket upgrades, or DNS rebinding. Protect segment-bounded `/api/v1`,
`/api/today`, and `/mcp` (including descendants and all methods). Leave
`/healthz`, static files, and SPA navigation outside this guard.
This is a browser boundary, not user authentication: native clients can omit
browser headers. Existing MCP capabilities and system-management owner Bearer
verification remain independent and unchanged.

## Policy

Run the guard inside the exception shield, before WebSocket processing and
endpoint execution. Reject with standard JSON 403 and stable codes
`request_host_forbidden` or `request_origin_forbidden` before any side effects.
For a connection accepted on a loopback local IP, allow only localhost,
loopback IP literals (including IPv6), or an exact `Perga:AllowedHosts` entry.
No DNS lookup, wildcard, suffix, forwarded header or remote-peer inference is
used. A missing local IP (TestServer/in-process transport) uses this same strict
policy. Non-loopback local connections retain their configured network exposure;
this guard does not add authentication for public deployments.

If Origin exists it must be one HTTP(S) absolute origin, with no credentials,
query, fragment or non-root path. Compare canonical scheme, host and effective
port to the request, or an exact configured `Perga:AllowedOrigins` origin.
Reject opaque `null`, malformed, empty, multiple and foreign origins.
Sec-Fetch-Site cross-site is rejected unless a valid Origin explicitly matches
AllowedOrigins. Unknown/multiple fetch metadata values also fail closed.
Host approval and origin approval are separate: neither bypasses the other.
Requests without browser headers remain usable by curl, CLI and MCP clients.

## Configuration and proxy operation

Both lists default empty and bind through existing PergaHostOptions conventions.
Invalid list entries fail startup. AllowedHosts contains bare DNS names or IPs,
without ports or wildcards. AllowedOrigins contains full HTTP(S) origins with
optional ports. Example: `Perga:AllowedHosts:0=perga.example` and
`Perga:AllowedOrigins:0=https://console.example`. These explicitly delegate
browser access; they do not bypass owner authorization on drain/reload.
No automatic trust of X-Forwarded-* or Forwarded headers is introduced.
Vite already uses changeOrigin and rewrites only its own browser Origin to the
backend target for both HTTP and WS, so its current proxy remains compatible.

Static files mount only at the root. The historical /next asset alias is removed
in coordination with the frontend root-base migration. Ordinary SPA fallback
continues to serve non-API navigation paths.

## Verification and removal scope

Integration tests cover HTTP and real TestServer WebSocket upgrades, malformed
and foreign origins, fetch metadata, host spoofing, native clients, explicit
allowlists, static/health exemptions and independent owner authorization.
Run the complete .NET solution and pinned Rust bridge checks on the final tree.
Two independent read-only L2 reviews are required before delivery.

Repository searches found no build/runtime/test dependency on tools/rust-terminal;
only README/DESIGN historical descriptions and bridge provenance comments refer
to it. Delete the seed, preserving the independently implemented bridge. Historical
documentation outside the authorized paths is left for the documentation wave.

## Review and validation record

Baseline: origin/main 15ec7d73. Two independent read-only L2 reviews
(ses_924a289ce32f47a0a82e84a15726eeb8 and
ses_45e86c54d382473faf1a7a3ee152ca7b) found no blockers. Both re-reviewed
the final raw-path validation fix and expanded coverage with no blockers.

Before the guard, all six foreign-Origin regression cases failed as expected;
GET /api/v1/areas returned 200. Additional parser regression cases reproduced
three inappropriate accepts caused by Uri dot-segment normalization. The fix
validates the original path as well as the parsed URI.

Final focused run: 57 RequestOriginGuardTests passed, including real Kestrel
HTTP and WS over TCP, explicit allowlisted WS, forwarded-header spoofing,
HTTPS/IPv6 authorities, independent drain/reload authorization, malformed
headers and rejected-write side effects. Static integration coverage verifies
root JS/CSS/manifest delivery and removal of the /next asset alias.

Commands run from the B1 worktree (SDK/tool directories added to PATH):

- `dotnet test tests/Perga.Host.IntegrationTests --filter FullyQualifiedName~RequestOriginGuardTests`
- `dotnet test tests/Perga.Host.IntegrationTests --filter FullyQualifiedName~InvalidOrDifferentOriginFailsClosed`
  (deliberately failing parser reproduction before the fix).
- `dotnet test Perga.sln` (complete solution).
- `(cd tools/perga-term-bridge && cargo fmt --check && cargo test --locked)`:
  format passed, 15 tests passed, no failures.
- `git diff --check origin/main` and scope inspection.

The first full run exposed a missing OAR bridge build artifact in this fresh
worktree (provider smoke test). Ran `npm ci --prefix tools/perga-oar-bridge`
and `npm run build --prefix tools/perga-oar-bridge` to supply ignored build
prerequisites; no OAR source or lockfile changes. No frontend, Kernel, SQLite,
library or active terminal-bridge source changes are part of this patch.
Root-only delivery must deploy together with P3's frontend root-base migration.

Final complete-solution run passed: 1,062 tests across eight projects, including
372 Host integration tests. No skipped tests or failures.
